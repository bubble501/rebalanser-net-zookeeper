using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using org.apache.zookeeper;
using Rebalanser.Core;
using Rebalanser.Core.Logging;
using Rebalanser.ZooKeeper.ResourceManagement;
using Rebalanser.ZooKeeper.Zk;

namespace Rebalanser.ZooKeeper.GlobalBarrier
{
    public class Follower : Watcher, IFollower
    {
        // services
        private IZooKeeperService zooKeeperService;
        private ILogger logger;
        private ResourceManager store;
        
        // immutable state
        private readonly string clientId;
        private readonly int clientNumber;
        private CancellationToken followerToken;
        private readonly TimeSpan sessionTimeout;
        private readonly TimeSpan onStartDelay;
        
        // mutable state
        private string watchSiblingPath;
        private string siblingId;
        private int statusVersion;
        private Task rebalancingTask;
        private CancellationTokenSource rebalancingCts;
        private BlockingCollection<FollowerEvent> events;
        private bool ignoreWatches;
        private Stopwatch disconnectedTimer;
        
        public Follower(IZooKeeperService zooKeeperService,
            ILogger logger,
            ResourceManager store,
            string clientId,
            int clientNumber,
            string watchSiblingPath,
            TimeSpan sessionTimeout,
            TimeSpan onStartDelay,
            CancellationToken followerToken)
        {
            this.zooKeeperService = zooKeeperService;
            this.logger = logger;
            this.store = store;
            this.clientId = clientId;
            this.clientNumber = clientNumber;
            this.watchSiblingPath = watchSiblingPath;
            this.siblingId = watchSiblingPath.Substring(watchSiblingPath.LastIndexOf("/", StringComparison.Ordinal));
            this.sessionTimeout = sessionTimeout;
            this.onStartDelay = onStartDelay;
            this.followerToken = followerToken;
            
            this.rebalancingCts = new CancellationTokenSource();
            this.events = new BlockingCollection<FollowerEvent>();
            this.disconnectedTimer = new Stopwatch();
        }
        
        // Important that nothing throws an exception in this method as it is called from the zookeeper library
        public override async Task process(WatchedEvent @event)
        {
            if (this.followerToken.IsCancellationRequested || this.ignoreWatches)
                return;
                
            if(@event.getPath() != null)
                this.logger.Info(this.clientId, $"Follower - KEEPER EVENT {@event.getState()} - {@event.get_Type()} - {@event.getPath()}");
            else 
                this.logger.Info(this.clientId, $"Follower - KEEPER EVENT {@event.getState()} - {@event.get_Type()}");
            
            switch (@event.getState())
            {
                case Event.KeeperState.Expired:
                    this.events.Add(FollowerEvent.SessionExpired);
                    break;
                case Event.KeeperState.Disconnected:
                    if(!this.disconnectedTimer.IsRunning)
                        this.disconnectedTimer.Start();
                    break;
                case Event.KeeperState.ConnectedReadOnly:
                case Event.KeeperState.SyncConnected:
                    if(this.disconnectedTimer.IsRunning)
                        this.disconnectedTimer.Reset();
                    
                    if (@event.get_Type() == Event.EventType.NodeDeleted)
                    {
                        if (@event.getPath().EndsWith(this.siblingId))
                        {
                            await PerformLeaderCheckAsync();
                        }
                        else
                        {
                            this.logger.Error(this.clientId, $"Follower - Unexpected node deletion detected of {@event.getPath()}");
                            this.events.Add(FollowerEvent.PotentialInconsistentState);
                        }
                    }
                    else if (@event.get_Type() == Event.EventType.NodeDataChanged)
                    {
                        if (@event.getPath().EndsWith("status"))
                            await SendTriggerRebalancingEvent();
                    }

                    break;
                default:
                    this.logger.Error(this.clientId,
                        $"Follower - Currently this library does not support ZooKeeper state {@event.getState()}");
                    this.events.Add(FollowerEvent.PotentialInconsistentState);
                    break;
            }
        }

        public async Task<BecomeFollowerResult> BecomeFollowerAsync()
        {
            try
            {
                this.ignoreWatches = false;
                await this.zooKeeperService.WatchSiblingNodeAsync(this.watchSiblingPath, this);
                this.logger.Info(this.clientId, $"Follower - Set a watch on sibling node {this.watchSiblingPath}");

                await this.zooKeeperService.WatchStatusAsync(this);
                this.logger.Info(this.clientId, $"Follower - Set a watch on status node");
            }
            catch (ZkNoEphemeralNodeWatchException)
            {
                this.logger.Info(this.clientId, "Follower - Could not set a watch on the sibling node as it has gone");
                return BecomeFollowerResult.WatchSiblingGone;
            }
            catch (Exception e)
            {
                this.logger.Error("Follower - Could not become a follower due to an error", e);
                return BecomeFollowerResult.Error;
            }

            return BecomeFollowerResult.Ok;
        }
        
        
        public async Task<FollowerExitReason> StartEventLoopAsync()
        {
            // it is possible that rebalancing has been triggered already, so check 
            // if any resources have been assigned already and if so, add a RebalancingTriggered event
            await CheckForRebalancingAsync();
            
            while (!this.followerToken.IsCancellationRequested)
            {
                if (this.disconnectedTimer.IsRunning && this.disconnectedTimer.Elapsed > this.sessionTimeout)
                {
                    this.zooKeeperService.SessionExpired();
                    await CleanUpAsync();
                    return FollowerExitReason.SessionExpired;
                }
                
                FollowerEvent followerEvent;
                if (this.events.TryTake(out followerEvent))
                {
                    switch (followerEvent)
                    {
                        case FollowerEvent.SessionExpired:
                            this.zooKeeperService.SessionExpired();
                            await CleanUpAsync();
                            return FollowerExitReason.SessionExpired;

                        case FollowerEvent.IsNewLeader:
                            await CleanUpAsync();
                            return FollowerExitReason.PossibleRoleChange;

                        case FollowerEvent.PotentialInconsistentState:
                            await CleanUpAsync();
                            return FollowerExitReason.PotentialInconsistentState;
                        
                        case FollowerEvent.FatalError:
                            await CleanUpAsync();
                            return FollowerExitReason.FatalError;

                        case FollowerEvent.RebalancingTriggered:
                            if (this.events.Any())
                            {
                                // skip this event. All other events take precedence over rebalancing
                                // there may be multiple rebalancing events, so if the events collection
                                // consists only of rebalancing events then we'll just process the last one
                            }
                            else
                            {
                                await CancelRebalancingIfInProgressAsync();
                                logger.Info(this.clientId, "Follower - Status change received");
                                rebalancingTask = Task.Run(async () =>
                                    await RespondToRebalancing(this.rebalancingCts.Token));
                            }

                            break;
                        
                        default:
                            await CleanUpAsync();
                            return FollowerExitReason.PotentialInconsistentState;
                    }
                }

                await WaitFor(TimeSpan.FromSeconds(1));
            }

            if (this.followerToken.IsCancellationRequested)
            {
                await CleanUpAsync();
                await this.zooKeeperService.CloseSessionAsync();
                return FollowerExitReason.Cancelled;
            }

            return FollowerExitReason.PotentialInconsistentState;
        }

        private async Task SendTriggerRebalancingEvent()
        {
            try
            {
                var status = await this.zooKeeperService.WatchStatusAsync(this);
                this.statusVersion = status.Version;
                this.events.Add(FollowerEvent.RebalancingTriggered);
            }
            catch (Exception e)
            {
                this.logger.Error("Follower - Could not put a watch on the status node", e);
                this.events.Add(FollowerEvent.PotentialInconsistentState);
            }
        }

        private async Task CheckForRebalancingAsync()
        {
            var resources = await this.zooKeeperService.GetResourcesAsync(null, null);
            var assignedResources = resources.ResourceAssignments.Assignments
                .Where(x => x.ClientId.Equals(this.clientId))
                .Select(x => x.Resource)
                .ToList();
            
            if(assignedResources.Any())
                this.events.Add(FollowerEvent.RebalancingTriggered);
        }
        
        private async Task RespondToRebalancing(CancellationToken rebalancingToken)
        {
            try
            {
                var result = await ProcessStatusChangeAsync(rebalancingToken);
                switch (result)
                {
                    case RebalancingResult.Complete:
                        logger.Info(this.clientId, "Follower - Status change complete");
                        break;

                    case RebalancingResult.Cancelled:
                        logger.Warn(this.clientId, "Follower - Status change cancelled");
                        break;

                    default:
                        this.logger.Error(this.clientId,
                            $"Follower - A non-supported RebalancingResult has been returned: {result}");
                        this.events.Add(FollowerEvent.PotentialInconsistentState);
                        break;
                }
            }
            catch (ZkSessionExpiredException)
            {
                this.logger.Warn(this.clientId, $"Follower - The session was lost during rebalancing");
                this.events.Add(FollowerEvent.SessionExpired);
            }
            catch (ZkOperationCancelledException)
            {
                this.logger.Warn(this.clientId, $"Follower - Status change cancelled");
            }
            catch (InconsistentStateException e)
            {
                this.logger.Error(this.clientId, $"Follower - An error occurred potentially leaving the client in an inconsistent state. Termination of the client or creationg of a new session will follow", e);
                this.events.Add(FollowerEvent.PotentialInconsistentState);
            }
            catch (TerminateClientException e)
            {
                this.logger.Error(this.clientId, $"Follower - A fatal error occurred, aborting", e);
                this.events.Add(FollowerEvent.FatalError);
            }
            catch (Exception e)
            {
                this.logger.Error(this.clientId, $"Follower - Rebalancing failed.", e);
                this.events.Add(FollowerEvent.PotentialInconsistentState);
            }
        }

        private async Task<RebalancingResult> ProcessStatusChangeAsync(CancellationToken rebalancingToken)
        {
            var status = await this.zooKeeperService.WatchStatusAsync(this);
            if(status.Version != this.statusVersion)
                this.logger.Warn(this.clientId, "Follower - The status has changed between the notification and response");
            
            if (rebalancingToken.IsCancellationRequested)
                return RebalancingResult.Cancelled;
                
            if (status.RebalancingStatus == RebalancingStatus.StopActivity)
            {
                this.logger.Info(this.clientId, "Follower - Status change received - stop activity");
                await this.store.InvokeOnStopActionsAsync(this.clientId, "Follower");
                
                if (rebalancingToken.IsCancellationRequested)
                    return RebalancingResult.Cancelled;
                
                await this.zooKeeperService.SetFollowerAsStopped(this.clientId);
                this.logger.Info(this.clientId, "Follower - Created follower stopped node");
            }
            else if (status.RebalancingStatus == RebalancingStatus.ResourcesGranted)
            {
                this.logger.Info(this.clientId, "Follower - Status change received - resources granted");
                var resources = await this.zooKeeperService.GetResourcesAsync(null, null);
                    
                var assignedResources = resources.ResourceAssignments.Assignments
                    .Where(x => x.ClientId.Equals(this.clientId))
                    .Select(x => x.Resource)
                    .ToList();
                
                this.logger.Info(this.clientId, $"Follower - {assignedResources.Count} resources granted");

                if (this.store.IsInStartedState())
                {
                    this.logger.Warn(this.clientId, "Follower - The resources granted status change has been received while already in the started state. Stopped all activity first");
                    await this.store.InvokeOnStopActionsAsync(this.clientId, "Follower");
                }
                
                if (this.onStartDelay.Ticks > 0)
                {
                    this.logger.Info(this.clientId, $"Follower - Delaying on start for {(int)this.onStartDelay.TotalMilliseconds}ms");
                    await WaitFor(this.onStartDelay, rebalancingToken);
                }
                
                if (rebalancingToken.IsCancellationRequested)
                    return RebalancingResult.Cancelled;
                
                await this.store.InvokeOnStartActionsAsync(this.clientId, "Follower", assignedResources, rebalancingToken, this.followerToken);
                
                if (rebalancingToken.IsCancellationRequested)
                    return RebalancingResult.Cancelled;
                
                await this.zooKeeperService.SetFollowerAsStarted(this.clientId);
                this.logger.Info(this.clientId, "Follower - Removed follower stopped node");
            }
            else if (status.RebalancingStatus == RebalancingStatus.StartConfirmed)
            {
                this.logger.Info(this.clientId, "Follower - All followers confirm started"); // no longer used
            }
            else
            {
                this.logger.Error(this.clientId, "Follower - Non-supported status received - ignoring");
            }
            
            return RebalancingResult.Complete;
        }
       
        private async Task CleanUpAsync()
        {
            try
            {
                this.ignoreWatches = true;
                await CancelRebalancingIfInProgressAsync();
            }
            finally
            {
                await this.store.InvokeOnStopActionsAsync(this.clientId, "Follower");
            }
        }
        
        private async Task CancelRebalancingIfInProgressAsync()
        {
            if (this.rebalancingTask != null && !this.rebalancingTask.IsCompleted)
            {
                logger.Info(this.clientId, "Follower - Cancelling the rebalancing that is in progress");
                this.rebalancingCts.Cancel();
                try
                {
                    await this.rebalancingTask; // might need to put a time limit on this
                }
                catch (Exception ex)
                {
                    this.logger.Error(this.clientId, "Follower - Errored on cancelling rebalancing", ex);
                    this.events.Add(FollowerEvent.PotentialInconsistentState);
                }
                this.rebalancingCts = new CancellationTokenSource(); // reset cts
            }
        }

        private async Task WaitFor(TimeSpan waitPeriod)
        {
            try
            {
                await Task.Delay(waitPeriod, this.followerToken);
            }
            catch (TaskCanceledException)
            {}
        }
        
        private async Task WaitFor(TimeSpan waitPeriod, CancellationToken rebalancingToken)
        {
            try
            {
                await Task.Delay(waitPeriod, rebalancingToken);
            }
            catch (TaskCanceledException)
            {}
        }
        
        private async Task PerformLeaderCheckAsync()
        {
            bool checkComplete = false;
            while (!checkComplete)
            {
                try
                {
                    int maxClientNumber = -1;
                    string watchChild = string.Empty;
                    var clients = await this.zooKeeperService.GetActiveClientsAsync();

                    foreach (var childPath in clients.ClientPaths)
                    {
                        int siblingClientNumber = int.Parse(childPath.Substring(childPath.Length - 10, 10));
                        if (siblingClientNumber > maxClientNumber && siblingClientNumber < this.clientNumber)
                        {
                            watchChild = childPath;
                            maxClientNumber = siblingClientNumber;
                        }
                    }

                    if (maxClientNumber == -1)
                    {
                        this.events.Add(FollowerEvent.IsNewLeader);
                    }
                    else
                    {
                        this.watchSiblingPath = watchChild;
                        this.siblingId = watchSiblingPath.Substring(watchChild.LastIndexOf("/", StringComparison.Ordinal));
                        await this.zooKeeperService.WatchSiblingNodeAsync(watchChild, this);
                        this.logger.Info(this.clientId, $"Follower - Set a watch on sibling node {this.watchSiblingPath}");
                    }

                    checkComplete = true;
                }
                catch (ZkNoEphemeralNodeWatchException)
                {
                    // do nothing except wait, the next iteration will find
                    // another client or it wil detect that it itself is the new leader
                    await WaitFor(TimeSpan.FromSeconds(1));
                }
                catch (ZkSessionExpiredException)
                {
                    this.events.Add(FollowerEvent.SessionExpired);
                    checkComplete = true;
                }
                catch (Exception ex)
                {
                    this.logger.Error(this.clientId, "Follower - Failed looking for sibling to watch", ex);
                    this.events.Add(FollowerEvent.PotentialInconsistentState);
                    checkComplete = true;
                }
            }
        }
    }
}