using System;
using System.Collections.Generic;
using Microsoft.Extensions.ObjectPool;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Utility;

namespace Robust.Shared.Physics.Systems
{
    public abstract class SharedBroadphaseSystem : EntitySystem
    {
        [Dependency] private readonly IMapManagerInternal _mapManager = default!;
        [Dependency] private readonly EntityLookupSystem _lookup = default!;
        [Dependency] private readonly SharedPhysicsSystem _physicsSystem = default!;

        private ISawmill _logger = default!;

        private const int MinimumBroadphaseCapacity = 256;

        /*
         * Okay so Box2D has its own "MoveProxy" stuff so you can easily find new contacts when required.
         * Our problem is that we have nested broadphases (rather than being on separate maps) which makes this
         * not feasible because a body could be intersecting 2 broadphases.
         * Hence we need to check which broadphases it does intersect and checkar for colliding bodies.
         */

        /// <summary>
        /// How much to expand bounds by to check cross-broadphase collisions.
        /// Ideally you want to set this to your largest body size.
        /// This only has a noticeable performance impact where multiple broadphases are in close proximity.
        /// </summary>
        private float _broadphaseExpand;

        private readonly ObjectPool<HashSet<FixtureProxy>> _proxyPool =
            new DefaultObjectPool<HashSet<FixtureProxy>>(new SetPolicy<FixtureProxy>(), 4096);

        private readonly Dictionary<FixtureProxy, HashSet<FixtureProxy>> _pairBuffer = new(64);

        public override void Initialize()
        {
            base.Initialize();

            _logger = Logger.GetSawmill("physics");
            UpdatesOutsidePrediction = true;

            UpdatesAfter.Add(typeof(SharedTransformSystem));

            var configManager = IoCManager.Resolve<IConfigurationManager>();
            configManager.OnValueChanged(CVars.BroadphaseExpand, SetBroadphaseExpand, true);
        }

        public override void Shutdown()
        {
            base.Shutdown();
            var configManager = IoCManager.Resolve<IConfigurationManager>();
            configManager.UnsubValueChanged(CVars.BroadphaseExpand, SetBroadphaseExpand);
        }

        private void SetBroadphaseExpand(float value) => _broadphaseExpand = value;

        #region Find Contacts

        /// <summary>
        /// Check the AABB for each moved broadphase fixture and add any colliding entities to the movebuffer in case.
        /// </summary>
        private void FindGridContacts(
            SharedPhysicsMapComponent component,
            MapId mapId,
            HashSet<IMapGrid> movedGrids,
            Dictionary<FixtureProxy, Box2> gridMoveBuffer,
            EntityQuery<BroadphaseComponent> broadQuery)
        {
            // None moved this tick
            if (movedGrids.Count == 0) return;

            var mapBroadphase = broadQuery.GetComponent(_mapManager.GetMapEntityId(mapId));

            // This is so that if we're on a broadphase that's moving (e.g. a grid) we need to make sure anything
            // we move over is getting checked for collisions, and putting it on the movebuffer is the easiest way to do so.
            var moveBuffer = component.MoveBuffer;

            foreach (var grid in movedGrids)
            {
                DebugTools.Assert(grid.ParentMapId == mapId);
                var worldAABB = grid.WorldAABB;
                var enlargedAABB = worldAABB.Enlarged(_broadphaseExpand);
                var state = (moveBuffer, gridMoveBuffer);

                QueryMapBroadphase(mapBroadphase.DynamicTree, ref state, enlargedAABB);
                QueryMapBroadphase(mapBroadphase.StaticTree, ref state, enlargedAABB);
            }

            foreach (var (proxy, worldAABB) in gridMoveBuffer)
            {
                moveBuffer[proxy] = worldAABB;
            }
        }

        private void QueryMapBroadphase(IBroadPhase broadPhase,
            ref (Dictionary<FixtureProxy, Box2>, Dictionary<FixtureProxy, Box2>) state,
            Box2 enlargedAABB)
        {
            // Easier to just not go over each proxy as we already unioned the fixture's worldaabb.
            broadPhase.QueryAabb(ref state, static (ref (
                    Dictionary<FixtureProxy, Box2> moveBuffer,
                    Dictionary<FixtureProxy, Box2> gridMoveBuffer) tuple,
                in FixtureProxy value) =>
            {
                // 99% of the time it's just going to be the broadphase (for now the grid) itself.
                // hence this body check makes this run significantly better.
                // Also check if it's not already on the movebuffer.
                if (tuple.moveBuffer.ContainsKey(value))
                    return true;

                // To avoid updating during iteration.
                // Don't need to transform as it's already in map terms.
                tuple.gridMoveBuffer[value] = value.AABB;
                return true;
            }, enlargedAABB, true);
        }

        [Obsolete("Use the overload with SharedPhysicsMapComponent")]
        internal void FindNewContacts(MapId mapId)
        {
            if (!TryComp<SharedPhysicsMapComponent>(_mapManager.GetMapEntityId(mapId), out var physicsMap))
                return;

            FindNewContacts(physicsMap, mapId);
        }

        /// <summary>
        /// Go through every single created, moved, or touched proxy on the map and try to find any new contacts that should be created.
        /// </summary>
        internal void FindNewContacts(SharedPhysicsMapComponent component, MapId mapId)
        {
            var moveBuffer = component.MoveBuffer;
            var movedGrids = _mapManager.GetMovedGrids(mapId);
            var gridMoveBuffer = new Dictionary<FixtureProxy, Box2>();

            var broadphaseQuery = GetEntityQuery<BroadphaseComponent>();
            var physicsQuery = GetEntityQuery<PhysicsComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();

            // Find any entities being driven over that might need to be considered
            FindGridContacts(component, mapId, movedGrids, gridMoveBuffer, broadphaseQuery);

            // There is some mariana trench levels of bullshit going on.
            // We essentially need to re-create Box2D's FindNewContacts but in a way that allows us to check every
            // broadphase intersecting a particular proxy instead of just on the 1 broadphase.
            // This means we can generate contacts across different broadphases.
            // If you have a better way of allowing for broadphases attached to grids then by all means code it yourself.

            // FindNewContacts is inherently going to be a lot slower than Box2D's normal version so we need
            // to cache a bunch of stuff to make up for it.
            var contactManager = component.ContactManager;

            // Handle grids first as they're not stored on map broadphase at all.
            HandleGridCollisions(mapId, contactManager, movedGrids, physicsQuery, xformQuery);

            DebugTools.Assert(moveBuffer.Count > 0 || _pairBuffer.Count == 0);

            foreach (var (proxy, worldAABB) in moveBuffer)
            {
                var proxyBody = proxy.Fixture.Body;
                DebugTools.Assert(!proxyBody.Deleted);

                var state = (this, proxy, worldAABB, _pairBuffer, xformQuery, broadphaseQuery);

                // Get every broadphase we may be intersecting.
                _mapManager.FindGridsIntersectingApprox(mapId, worldAABB.Enlarged(_broadphaseExpand), ref state,
                    static (IMapGrid grid, ref (
                        SharedBroadphaseSystem system,
                        FixtureProxy proxy,
                        Box2 worldAABB,
                        Dictionary<FixtureProxy, HashSet<FixtureProxy>> pairBuffer,
                        EntityQuery<TransformComponent> xformQuery,
                        EntityQuery<BroadphaseComponent> broadphaseQuery) tuple) =>
                    {
                        tuple.system.FindPairs(tuple.proxy, tuple.worldAABB, grid.GridEntityId, tuple.pairBuffer, tuple.xformQuery, tuple.broadphaseQuery);
                        return true;
                    });

                FindPairs(proxy, worldAABB, _mapManager.GetMapEntityId(mapId), _pairBuffer, xformQuery, broadphaseQuery);
            }

            foreach (var (proxyA, proxies) in _pairBuffer)
            {
                var proxyABody = proxyA.Fixture.Body;

                foreach (var other in proxies)
                {
                    var otherBody = other.Fixture.Body;
                    // Because we may be colliding with something asleep (due to the way grid movement works) need
                    // to make sure the contact doesn't fail.
                    // This is because we generate a contact across 2 different broadphases where both bodies aren't
                    // moving locally but are moving in world-terms.
                    if (proxyA.Fixture.Hard && other.Fixture.Hard &&
                        (gridMoveBuffer.ContainsKey(proxyA) || gridMoveBuffer.ContainsKey(other)))
                    {
                        _physicsSystem.WakeBody(proxyABody);
                        _physicsSystem.WakeBody(otherBody);
                    }

                    contactManager.AddPair(proxyA, other);
                }
            }

            foreach (var (_, proxies) in _pairBuffer)
            {
                _proxyPool.Return(proxies);
            }

            _pairBuffer.Clear();
            moveBuffer.Clear();
            _mapManager.ClearMovedGrids(mapId);
        }

        private void HandleGridCollisions(
            MapId mapId,
            ContactManager contactManager,
            HashSet<IMapGrid> movedGrids,
            EntityQuery<PhysicsComponent> bodyQuery,
            EntityQuery<TransformComponent> xformQuery)
        {
            var gridsPool = new List<MapGrid>();

            foreach (var grid in movedGrids)
            {
                DebugTools.Assert(grid.ParentMapId == mapId);

                var mapGrid = (MapGrid)grid;
                var xform = xformQuery.GetComponent(grid.GridEntityId);

                var (worldPos, worldRot, worldMatrix, invWorldMatrix) = xform.GetWorldPositionRotationMatrixWithInv(xformQuery);

                var aabb = new Box2Rotated(grid.LocalAABB, worldRot).CalcBoundingBox().Translated(worldPos);

                // TODO: Need to handle grids colliding with non-grid entities with the same layer
                // (nothing in SS14 does this yet).

                var transform = _physicsSystem.GetPhysicsTransform(grid.GridEntityId, xformQuery: xformQuery);
                gridsPool.Clear();

                foreach (var colliding in _mapManager.FindGridsIntersecting(mapId, aabb, gridsPool, xformQuery, bodyQuery, true))
                {
                    if (grid == colliding) continue;

                    var otherGrid = (MapGrid)colliding;
                    var otherGridBounds = colliding.WorldAABB;
                    var otherGridInvMatrix = colliding.InvWorldMatrix;
                    var otherTransform = _physicsSystem.GetPhysicsTransform(colliding.GridEntityId, xformQuery: xformQuery);

                    // Get Grid2 AABB in grid1 ref
                    var aabb1 = grid.LocalAABB.Intersect(invWorldMatrix.TransformBox(otherGridBounds));

                    // TODO: AddPair has a nasty check in there that's O(n) but that's also a general physics problem.
                    var ourChunks = mapGrid.GetLocalMapChunks(aabb1);

                    // Only care about chunks on other grid overlapping us.
                    while (ourChunks.MoveNext(out var ourChunk))
                    {
                        var ourChunkWorld = worldMatrix.TransformBox(ourChunk.CachedBounds.Translated(ourChunk.Indices * grid.ChunkSize));
                        var ourChunkOtherRef = otherGridInvMatrix.TransformBox(ourChunkWorld);
                        var collidingChunks = otherGrid.GetLocalMapChunks(ourChunkOtherRef);

                        while (collidingChunks.MoveNext(out var collidingChunk))
                        {
                            foreach (var fixture in ourChunk.Fixtures)
                            {
                                for (var i = 0; i < fixture.Shape.ChildCount; i++)
                                {
                                    var fixAABB = fixture.Shape.ComputeAABB(transform, i);

                                    foreach (var otherFixture in collidingChunk.Fixtures)
                                    {
                                        for (var j = 0; j < otherFixture.Shape.ChildCount; j++)
                                        {
                                            var otherAABB = otherFixture.Shape.ComputeAABB(otherTransform, j);

                                            if (!fixAABB.Intersects(otherAABB)) continue;
                                            contactManager.AddPair(fixture, i, otherFixture, j, ContactFlags.Grid);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        private void FindPairs(
            FixtureProxy proxy,
            Box2 worldAABB,
            EntityUid broadphase,
            Dictionary<FixtureProxy, HashSet<FixtureProxy>> pairBuffer,
            EntityQuery<TransformComponent> xformQuery,
            EntityQuery<BroadphaseComponent> broadphaseQuery)
        {
            DebugTools.Assert(proxy.Fixture.Body.CanCollide);

            var proxyBody = proxy.Fixture.Body;

            // Broadphase can't intersect with entities on itself so skip.
            if (proxyBody.Owner == broadphase || !xformQuery.TryGetComponent(proxyBody.Owner, out var xform))
            {
                return;
            }

            // Logger.DebugS("physics", $"Checking proxy for {proxy.Fixture.Body.Owner} on {broadphase.Owner}");
            Box2 aabb;
            var proxyBroad = _lookup.GetBroadphase(proxy.Fixture.Body.Owner, xform, broadphaseQuery, xformQuery);

            if (proxyBroad == null)
            {
                _logger.Error($"Found null broadphase for {ToPrettyString(proxy.Fixture.Body.Owner)}");
                DebugTools.Assert(false);
                return;
            }

            // If it's the same broadphase as our body's one then don't need to translate the AABB.
            if (proxyBroad.Owner == broadphase)
            {
                aabb = proxy.AABB;
            }
            else
            {
                var broadXform = xformQuery.GetComponent(broadphase);
                aabb = broadXform.InvWorldMatrix.TransformBox(worldAABB);
            }

            var broadphaseComp = broadphaseQuery.GetComponent(broadphase);

            if (!pairBuffer.TryGetValue(proxy, out var proxyPairs))
            {
                proxyPairs = _proxyPool.Get();
                pairBuffer[proxy] = proxyPairs;
            }

            var state = (proxyPairs, pairBuffer, proxy);

            QueryBroadphase(broadphaseComp.DynamicTree, ref state, aabb);

            if ((proxy.Fixture.Body.BodyType & BodyType.Static) != 0x0)
                return;

            QueryBroadphase(broadphaseComp.StaticTree, ref state, aabb);
        }

        private void QueryBroadphase(IBroadPhase broadPhase, ref (HashSet<FixtureProxy>, Dictionary<FixtureProxy, HashSet<FixtureProxy>>, FixtureProxy) state, Box2 aabb)
        {
            broadPhase.QueryAabb(ref state, static (
                ref (HashSet<FixtureProxy> proxyPairs, Dictionary<FixtureProxy, HashSet<FixtureProxy>> pairBuffer, FixtureProxy proxy) tuple,
                in FixtureProxy other) =>
            {
                DebugTools.Assert(other.Fixture.Body.CanCollide);
                // Logger.DebugS("physics", $"Checking {proxy.Fixture.Body.Owner} against {other.Fixture.Body.Owner} at {aabb}");

                if (tuple.proxy == other ||
                    !ContactManager.ShouldCollide(tuple.proxy.Fixture, other.Fixture) ||
                    tuple.proxy.Fixture.Body == other.Fixture.Body)
                {
                    return true;
                }

                // Don't add duplicates.
                // Look it disgusts me but we can't do it Box2D's way because we're getting pairs
                // with different broadphases so can't use Proxy sorting to skip duplicates.
                if (tuple.proxyPairs.Contains(other) ||
                    tuple.pairBuffer.TryGetValue(other, out var otherPairs) && otherPairs.Contains(tuple.proxy))
                {
                    return true;
                }

                tuple.proxyPairs.Add(other);
                return true;
            }, aabb, true);
        }

        public void RegenerateContacts(PhysicsComponent body)
        {
            _physicsSystem.DestroyContacts(body);
            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();

            var broadphase = _lookup.GetBroadphase(body.Owner, xformQuery.GetComponent(body.Owner), broadQuery, xformQuery);

            if (broadphase != null)
            {
                var mapId = EntityManager.GetComponent<TransformComponent>(body.Owner).MapID;

                foreach (var fixture in EntityManager.GetComponent<FixturesComponent>(body.Owner).Fixtures.Values)
                {
                    TouchProxies(mapId, broadphase, fixture);
                }
            }
        }

        private void TouchProxies(MapId mapId, BroadphaseComponent broadphase, Fixture fixture)
        {
            var broadphasePos = Transform(broadphase.Owner).WorldMatrix;

            foreach (var proxy in fixture.Proxies)
            {
                AddToMoveBuffer(mapId, proxy, broadphasePos.TransformBox(proxy.AABB));
            }
        }

        private void AddToMoveBuffer(MapId mapId, FixtureProxy proxy, Box2 aabb)
        {
            if (!TryComp<SharedPhysicsMapComponent>(_mapManager.GetMapEntityId(mapId), out var physicsMap))
                return;

            DebugTools.Assert(proxy.Fixture.Body.CanCollide);

            physicsMap.MoveBuffer[proxy] = aabb;
        }

        public void Refilter(Fixture fixture)
        {
            // TODO: Call this method whenever collisionmask / collisionlayer changes
            // TODO: This should never becalled when body is null.
            DebugTools.Assert(fixture.Body != null);
            if (fixture.Body == null)
            {
                return;
            }

            foreach (var (_, contact) in fixture.Contacts)
            {
                contact.Flags |= ContactFlags.Filter;
            }

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            var broadphase = _lookup.GetBroadphase(fixture.Body.Owner, xformQuery.GetComponent(fixture.Body.Owner), broadQuery, xformQuery);

            // If nullspace or whatever ignore it.
            if (broadphase == null) return;

            TouchProxies(Transform(fixture.Body.Owner).MapID, broadphase, fixture);
        }

        // TODO: The below is slow and should just query the map's broadphase directly. The problem is that
        // there's some ordering stuff going on where the broadphase has queued all of its updates but hasn't applied
        // them yet so this query will fail on initialization which chains into a whole lot of issues.
        internal IEnumerable<BroadphaseComponent> GetBroadphases(MapId mapId, Box2 aabb)
        {
            // TODO Okay so problem: If we just do Encloses that's a lot faster BUT it also means we don't return the
            // map's broadphase which avoids us iterating over it for 99% of bodies.

            if (mapId == MapId.Nullspace) yield break;

            foreach (var (broadphase, xform) in EntityManager.EntityQuery<BroadphaseComponent, TransformComponent>(true))
            {
                if (xform.MapID != mapId) continue;

                if (!EntityManager.TryGetComponent(broadphase.Owner, out IMapGridComponent? mapGrid))
                {
                    yield return broadphase;
                    continue;
                }

                var grid = (IMapGridInternal) mapGrid.Grid;

                // Won't worry about accurate bounds checks as it's probably slower in most use cases.
                var chunkEnumerator = grid.GetMapChunks(aabb);

                if (chunkEnumerator.MoveNext(out _))
                {
                    yield return broadphase;
                }
            }
        }
    }
}
