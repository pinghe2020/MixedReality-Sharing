// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.Sharing.Matchmaking;
using System;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;
using Xunit.Sdk;

namespace Matchmaking.Local.Test
{
    public abstract class LocalMatchmakingTest
    {
        Func<int, IMatchmakingService> matchmakingServiceFactory_;

        protected LocalMatchmakingTest(Func<int, IMatchmakingService> matchmakingServiceFactory)
        {
            matchmakingServiceFactory_ = matchmakingServiceFactory;
        }

        private static int TestTimeoutMs
        {
            get
            {
                return Debugger.IsAttached ? Timeout.Infinite : 10000;
            }
        }

        private static void AssertSameAttributes(IRoom a, IRoom b)
        {
            Assert.Equal(a.Attributes.Count, b.Attributes.Count);
            foreach (var entry in a.Attributes)
            {
                Assert.Equal(entry.Value, b.Attributes[entry.Key]);
            }
        }

        [Fact]
        public void CreateRoom()
        {
            using (var cts = new CancellationTokenSource(TestTimeoutMs))
            using (var svc1 = matchmakingServiceFactory_(1))
            {
                var room1 = svc1.CreateRoomAsync("CreateRoom", "http://room1", null, cts.Token).Result;

                Assert.Equal("http://room1", room1.Connection);
                Assert.Empty(room1.Attributes);

                var attributes = new Dictionary<string, string> { ["prop1"] = "1", ["prop2"] = "2" };
                var room2 = svc1.CreateRoomAsync("CreateRoom", "foo://room2", attributes, cts.Token).Result;

                Assert.Equal("foo://room2", room2.Connection);
                Assert.Equal("1", room2.Attributes["prop1"]);
                Assert.Equal("2", room2.Attributes["prop2"]);
            }
        }

        private void AssertSame(IRoom lhs, IRoom rhs)
        {
            // ID is equal.
            Assert.Equal(lhs.Connection, rhs.Connection);

            // Attributes are equal.
            //var lAttributes = lhs.Attributes.OrderBy(a => a.Key);
            //var rAttributes = rhs.Attributes.OrderBy(a => a.Key);
            //Assert.True(lAttributes.SequenceEqual(rAttributes));
        }

        private bool SameRoom(IRoom lhs, IRoom rhs)
        {
            if (lhs.Connection != rhs.Connection) return false;

            // Attributes are equal.
            //var lAttributes = lhs.Attributes.OrderBy(a => a.Key);
            //var rAttributes = rhs.Attributes.OrderBy(a => a.Key);
            //return lAttributes.SequenceEqual(rAttributes);
            return true;
        }

        class RaiiGuard : IDisposable
        {
            private Action Quit { get; set; }
            public RaiiGuard(Action init, Action quit)
            {
                Quit = quit;
                if (init != null) init();
            }
            void IDisposable.Dispose()
            {
                if (Quit != null) Quit();
            }
        }

        // Run a query and wait for the predicate to be satisfied.
        // Return the list of rooms which satisfied the predicate or null if cancelled before the preducate was satisfied.
        private IList<IRoom> QueryAndWaitForRoomsPredicate(
            IMatchmakingService svc, string type,
            Func<IList<IRoom>, bool> pred, CancellationToken token)
        {
            using (var result = svc.StartDiscovery(type))
            {
                var rooms = result.Rooms;
                bool predicateResult = pred(rooms);
                if (predicateResult)
                {
                    return rooms; // optimistic path
                }
                using (var wakeUp = new AutoResetEvent(false))
                {
                    Action<IDiscoveryTask> onChange = (IDiscoveryTask sender) => wakeUp.Set();

                    using (var unregisterCancel = token.Register(() => wakeUp.Set()))
                    using (var unregisterWatch = new RaiiGuard(() => result.Updated += onChange, () => result.Updated -= onChange))
                    {
                        while (true)
                        {
                            rooms = result.Rooms;
                            if (pred(rooms))
                            {
                                return rooms;
                            }
                            wakeUp.WaitOne(); // wait for cancel or update
                            if (token.IsCancellationRequested)
                            {
                                return null;
                            }
                        }
                    }
                }
            }
        }

#if false
        private void AssertSame(IRoom lhs, IRoom rhs)
        {
            AssertSame(lhs, (IRoomInfo)rhs);

            var lParticipants = lhs.Participants.OrderBy(p => p.IdInRoom);
            var rParticipants = rhs.Participants.OrderBy(p => p.IdInRoom);
            // Participant IDs in room are equal.
            Assert.True(lParticipants.Select(p => p.IdInRoom).SequenceEqual(rParticipants.Select(p => p.IdInRoom)));
            // Match participant IDs are equal.
            Assert.True(lParticipants.Select(p => p.MatchParticipant.Id).SequenceEqual(rParticipants.Select(p => p.MatchParticipant.Id)));
        }
#endif
        [Fact]
        public void FindRoomsLocalAndRemote()
        {
            using (var cts = new CancellationTokenSource(TestTimeoutMs))
            using (var svc1 = matchmakingServiceFactory_(1))
            using (var svc2 = matchmakingServiceFactory_(2))
            {
                // Create some rooms in the first one
                const string category = "FindRoomsLocalAndRemote";
                var room1 = svc1.CreateRoomAsync(category, "Conn1", null, cts.Token).Result;
                var room2 = svc1.CreateRoomAsync(category, "Conn2", null, cts.Token).Result;
                var room3 = svc1.CreateRoomAsync(category, "Conn3", null, cts.Token).Result;

                // Discover them from the first service
                {
                    var rooms = QueryAndWaitForRoomsPredicate(svc1, category, rl => rl.Count >= 3, cts.Token);
                    Assert.Equal(3, rooms.Count());
                    Assert.Contains(rooms, r => r.UniqueId.Equals(room1.UniqueId));
                    Assert.Contains(rooms, r => r.UniqueId.Equals(room2.UniqueId));
                    Assert.Contains(rooms, r => r.UniqueId.Equals(room3.UniqueId));
                }

                // And also from the second
                {
                    var rooms = QueryAndWaitForRoomsPredicate(svc2, category, rl => rl.Count >= 3, cts.Token);
                    Assert.Equal(3, rooms.Count());
                    Assert.Contains(rooms, r => r.UniqueId.Equals(room1.UniqueId));
                    Assert.Contains(rooms, r => r.UniqueId.Equals(room2.UniqueId));
                    Assert.Contains(rooms, r => r.UniqueId.Equals(room3.UniqueId));
                }
            }
        }

        [Fact]
        public void FindRoomsFromAnnouncement()
        {
            // start discovery, then start services afterwards

            using (var cts = new CancellationTokenSource(TestTimeoutMs))
            using (var svc1 = matchmakingServiceFactory_(1))
            using (var svc2 = matchmakingServiceFactory_(2))
            {
                const string category = "FindRoomsFromAnnouncement";

                using (var task1 = svc1.StartDiscovery(category))
                using (var task2 = svc2.StartDiscovery(category))
                {
                    Assert.Empty(task1.Rooms);
                    Assert.Empty(task2.Rooms);

                    var room1 = svc1.CreateRoomAsync(category, "foo1", null, cts.Token).Result;

                    // local
                    var res1 = QueryAndWaitForRoomsPredicate(svc1, category, rl => rl.Any(), cts.Token);
                    Assert.Single(res1);
                    Assert.Equal(res1.First().UniqueId, room1.UniqueId);
                    // remote
                    var res2 = QueryAndWaitForRoomsPredicate(svc2, category, rl => rl.Any(), cts.Token);
                    Assert.Single(res1);
                    Assert.Equal(res1.First().UniqueId, room1.UniqueId);
                }
            }
        }
#if false
        [Fact]
        public void JoinRoomById()
        {
            using (var cts = new CancellationTokenSource(TestTimeoutMs))
            using (var svc1 = MakeMatchmakingService(1))
            using (var svc2 = MakeMatchmakingService(2))
            {
                // Create rooms from service1
                var attributes1 = new Dictionary<string, string> { ["Id"] = "room1", ["prop1"] = "1", ["prop2"] = "2" };
                var attributes2 = new Dictionary<string, string> { ["Id"] = "room2", ["prop1"] = "1", ["prop2"] = "2" };
                var room1 = svc1.CreateRoomAsync("conn1", attributes1, cts.Token).Result;
                var room2 = svc1.CreateRoomAsync("conn2", attributes2, cts.Token).Result;

                // discover from service2
                {
                    var req1 = new Dictionary<string, string> { ["Id"] = "room1" };
                    var rooms = svc2.StartDiscovery(req1);
                    WaitForCollectionPredicate(rooms, () => rooms.Count > 0, cts.Token);
                    svc2.StopDiscovery(rooms);
                    Assert.Single(rooms);
                    AssertSame(rooms.First(), room1);
                }

                {
                    var req2 = new Dictionary<string, string> { ["Id"] = "room2" };
                    var rooms = svc2.StartDiscovery(req2);
                    WaitForCollectionPredicate(rooms, () => rooms.Count > 0, cts.Token);
                    svc2.StopDiscovery(rooms);
                    Assert.Single(rooms);
                    AssertSame(rooms.First(), room2);
                }
            }
        }

        [Fact]
        public void Mix()
        {
            using (var svc1 = MakeMatchmakingService(1))
            using (var svc2 = MakeMatchmakingService(2))
            using (var svc3 = MakeMatchmakingService(3))
            {
                var room1 = svc1.CreateRoomAsync(
                    "MixRoomConn",
                    new Dictionary<string, string> { ["prop1"] = "1", ["prop2"] = "2" }
                    ).Result;

#if false
                IRoom foundRoom = null;
                {
                    var rooms = svc2.StartDiscovery(null);
                    var ev = new AutoResetEvent(false);
                    roomList.ListUpdated += (object sender, IRoomList updated) =>
                    {
                        var list = updated.Rooms;
                        Assert.Single(list);
                        foundRoom = list.ElementAt(0);
                        Assert.Equal(foundRoom.Id, room1.Id);
                        ev.Set();
                    };
                    ev.WaitOne(TestTimeoutMs);
                }
                Assert.NotNull(foundRoom);
                Assert.Equal(room1.Id, foundRoom.Id);
                {
                    var cts = new CancellationTokenSource(TestTimeoutMs);
                    while (room2.Attributes.Count != room1.Attributes.Count)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                    }
                }
                Assert.Equal(room1.Attributes, room2.Attributes);


                room2.SetAttributesAsync(new Dictionary<string, string> { ["prop1"] = "42" }).Wait();
                Assert.Equal(42, room2.Attributes["prop1"]);
                {
                    var cts = new CancellationTokenSource(TestTimeoutMs);
                    while (!room1.Attributes["prop1"].Equals(42))
                    {
                        cts.Token.ThrowIfCancellationRequested();
                    }
                }
                Assert.Equal(2, room1.Participants.Count());
                Assert.Equal(2, room2.Participants.Count());

                var room3 = (RoomBase)ctx3.Service.JoinRandomRoomAsync().Result;
                Assert.Equal(room1.Id, room3.Id);
                {
                    var cts = new CancellationTokenSource(TestTimeoutMs);
                    while (room3.Attributes.Count != 2)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                    }
                }

                AssertSameAttributes(room1, room3);
                Assert.Equal(3, room1.Participants.Count());
                Assert.Equal(3, room2.Participants.Count());
                Assert.Equal(3, room3.Participants.Count());

                room2.SendMessage(room2.Participants.First(p => p.MatchParticipant != null && p.MatchParticipant.Id.Equals(ctx3.PFactory.LocalParticipantId)), Encoding.UTF8.GetBytes("hello"));
                {
                    var ev = new ManualResetEventSlim();
                    room3.MessageReceived += (object o, MessageReceivedArgs args) =>
                    {
                        Assert.Equal(ctx2.PFactory.LocalParticipantId, args.Sender.MatchParticipant.Id);
                        Assert.Equal("hello", Encoding.UTF8.GetString(args.Payload));
                        ev.Set();
                    };

                    var cts = new CancellationTokenSource(TestTimeoutMs);
                    ev.Wait(cts.Token);
                }
#endif
            }
        }
#endif
    }

    public class LocalMatchmakingTestUdp : LocalMatchmakingTest
    {
        static private IMatchmakingService MakeMatchmakingService(int userIndex)
        {
            var net = new UdpPeerNetwork(new IPEndPoint(0xffffff7f, 45277), new IPEndPoint(0x0000007f + (userIndex << 24), 45277));
            return new PeerMatchmakingService(net);
        }

        public LocalMatchmakingTestUdp() : base(MakeMatchmakingService) { }
    }

    public class LocalMatchmakingTestMemory : LocalMatchmakingTest
    {
        static private IMatchmakingService MakeMatchmakingService(int userIndex)
        {
            var net = new MemoryPeerNetwork(userIndex);
            return new PeerMatchmakingService(net);
        }

        public LocalMatchmakingTestMemory()
            : base(MakeMatchmakingService)
        {

        }
    }
}