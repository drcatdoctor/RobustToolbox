using JetBrains.Annotations;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.BroadPhase;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Utility;
using System;
using System.Collections.Generic;

namespace Robust.Shared.GameObjects
{
    [Flags]
    public enum LookupFlags : byte
    {
        None = 0,

        /// <summary>
        /// Should we use the approximately intersecting entities or check tighter bounds.
        /// </summary>
        Approximate = 1 << 0,

        /// <summary>
        /// Should we query dynamic physics bodies.
        /// </summary>
        Dynamic = 1 << 1,

        /// <summary>
        /// Should we query static physics bodies.
        /// </summary>
        Static = 1 << 2,

        /// <summary>
        /// Should we query non-collidable physics bodies.
        /// </summary>
        Sundries = 1 << 3,

        /// <summary>
        /// Also return entities from an anchoring query.
        /// </summary>
        [Obsolete("Use Static")]
        Anchored = 1 << 4,

        /// <summary>
        /// Include entities that are currently in containers.
        /// </summary>
        Contained = 1 << 5,

        Uncontained = Dynamic | Static | Sundries,

        StaticSundries = Static | Sundries,
    }

    public sealed partial class EntityLookupSystem : EntitySystem
    {
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly SharedContainerSystem _container = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;

        /// <summary>
        /// Returns all non-grid entities. Consider using your own flags if you wish for a faster query.
        /// </summary>
        public const LookupFlags DefaultFlags = LookupFlags.Contained | LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Sundries;

        private const int GrowthRate = 256;

        private const float PointEnlargeRange = .00001f / 2;

        /// <summary>
        /// Like RenderTree we need to enlarge our lookup range for EntityLookupComponent as an entity is only ever on
        /// 1 EntityLookupComponent at a time (hence it may overlap without another lookup).
        /// </summary>
        private float _lookupEnlargementRange;

        public override void Initialize()
        {
            base.Initialize();
            var configManager = IoCManager.Resolve<IConfigurationManager>();
            configManager.OnValueChanged(CVars.LookupEnlargementRange, value => _lookupEnlargementRange = value, true);

            SubscribeLocalEvent<BroadphaseComponent, ComponentAdd>(OnBroadphaseAdd);
            SubscribeLocalEvent<GridAddEvent>(OnGridAdd);
            SubscribeLocalEvent<MapChangedEvent>(OnMapChange);

            SubscribeLocalEvent<MoveEvent>(OnMove);
            SubscribeLocalEvent<EntParentChangedMessage>(OnParentChange);
            SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnContainerInsert);
            SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnContainerRemove);

            SubscribeLocalEvent<PhysicsComponent, PhysicsBodyTypeChangedEvent>(OnBodyTypeChange);
            SubscribeLocalEvent<CollisionChangeEvent>(OnPhysicsUpdate);

            EntityManager.EntityInitialized += OnEntityInit;
        }

        public override void Shutdown()
        {
            base.Shutdown();
            EntityManager.EntityInitialized -= OnEntityInit;
        }

        /// <summary>
        /// Updates the entity's AABB. Uses <see cref="ILookupWorldBox2Component"/>
        /// </summary>
        [UsedImplicitly]
        public void UpdateBounds(EntityUid uid, TransformComponent? xform = null, MetaDataComponent? meta = null)
        {
            if (_container.IsEntityInContainer(uid, meta))
                return;

            var xformQuery = GetEntityQuery<TransformComponent>();

            if (!xformQuery.Resolve(uid, ref xform))
                return;

            // also ensure that no parent is in a container.
            DebugTools.Assert(!_container.IsEntityOrParentInContainer(uid, meta, xform, null, xformQuery));

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var lookup = GetBroadphase(uid, xform, broadQuery, xformQuery);

            if (lookup == null) return;

            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);
            // If we're contained then LocalRotation should be 0 anyway.
            var aabb = GetAABBNoContainer(xform.Owner, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation);

            // TODO: Only container children need updating so could manually do this slightly better.
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation);
        }

        #region DynamicTree

        private void OnMapChange(MapChangedEvent ev)
        {
            if (ev.Created && ev.Map != MapId.Nullspace)
            {
                EnsureComp<BroadphaseComponent>(_mapManager.GetMapEntityId(ev.Map));
            }
        }

        private void OnGridAdd(GridAddEvent ev)
        {
            // Must be done before initialization as that's when broadphase data starts getting set.
            EnsureComp<BroadphaseComponent>(ev.EntityUid);
        }

        private void OnBroadphaseAdd(EntityUid uid, BroadphaseComponent component, ComponentAdd args)
        {
            component.DynamicTree = new DynamicTreeBroadPhase();
            component.StaticTree = new DynamicTreeBroadPhase();
            component.StaticSundriesTree = new DynamicTree<EntityUid>(
                (in EntityUid value) => GetTreeAABB(value, component.Owner));
            component.SundriesTree = new DynamicTree<EntityUid>(
                (in EntityUid value) => GetTreeAABB(value, component.Owner));
        }

        private Box2 GetTreeAABB(EntityUid entity, EntityUid tree)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();

            if (!xformQuery.TryGetComponent(entity, out var xform))
            {
                Logger.Error($"Entity tree contains a deleted entity? Tree: {ToPrettyString(tree)}, entity: {entity}");
                return default;
            }

            if (xform.ParentUid == tree)
                return GetAABBNoContainer(entity, xform.LocalPosition, xform.LocalRotation);

            if (!xformQuery.TryGetComponent(tree, out var treeXform))
            {
                Logger.Error($"Entity tree has no transform? Tree Uid: {tree}");
                return default;
            }

            return treeXform.InvWorldMatrix.TransformBox(GetWorldAABB(entity, xform));
        }

        internal void CreateProxies(Fixture fixture, Vector2 worldPos, Angle worldRot)
        {
            // TODO: Grids on broadphasecomponent
            if (_mapManager.IsGrid(fixture.Body.Owner))
                return;

            var xformQuery = GetEntityQuery<TransformComponent>();
            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xform = xformQuery.GetComponent(fixture.Body.Owner);
            var broadphase = GetBroadphase(fixture.Body.Owner, xformQuery.GetComponent(fixture.Body.Owner), broadQuery, xformQuery);

            if (broadphase == null || xform.MapUid == null)
            {
                throw new InvalidOperationException();
            }

            var mapTransform = new Transform(worldPos, worldRot);
            var (_, broadWorldRot, _, broadInvMatrix) = xformQuery.GetComponent(broadphase.Owner).GetWorldPositionRotationMatrixWithInv();
            var broadphaseTransform = new Transform(broadInvMatrix.Transform(mapTransform.Position), mapTransform.Quaternion2D.Angle - broadWorldRot);
            var moveBuffer = Comp<SharedPhysicsMapComponent>(xform.MapUid.Value).MoveBuffer;
            var tree = fixture.Body.BodyType == BodyType.Static ? broadphase.StaticTree : broadphase.DynamicTree;
            DebugTools.Assert(fixture.ProxyCount == 0);

            AddOrMoveProxies(fixture, tree, broadphaseTransform, mapTransform, moveBuffer);
        }

        internal void DestroyProxies(Fixture fixture, TransformComponent xform)
        {
            if (_mapManager.IsGrid(fixture.Body.Owner))
                return;

            if (fixture.ProxyCount == 0)
            {
                Logger.Warning($"Tried to destroy fixture {fixture.ID} on {ToPrettyString(fixture.Body.Owner)} that already has no proxies?");
                return;
            }

            var xformQuery = GetEntityQuery<TransformComponent>();
            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var broadphase = GetBroadphase(fixture.Body.Owner, xformQuery.GetComponent(fixture.Body.Owner), broadQuery, xformQuery);

            if (broadphase == null || xform.MapUid == null)
            {
                throw new InvalidOperationException();
            }

            var tree = fixture.Body.BodyType == BodyType.Static ? broadphase.StaticTree : broadphase.DynamicTree;
            var moveBuffer = Comp<SharedPhysicsMapComponent>(xform.MapUid.Value).MoveBuffer;
            DestroyProxies(fixture, tree, moveBuffer);
        }

        #endregion

        #region Entity events

        private void OnPhysicsUpdate(ref CollisionChangeEvent ev)
        {
            if (HasComp<IMapGridComponent>(ev.Body.Owner))
                return;

            var xformQuery = GetEntityQuery<TransformComponent>();
            var xform = xformQuery.GetComponent(ev.Body.Owner);

            if (!ev.CanCollide && _container.IsEntityOrParentInContainer(ev.Body.Owner, null, xform, null, xformQuery))
            {
                // getting inserted, skip sundries insertion and just let container insertion handle tree removal.

                // TODO: for whatever fucking cursed reason, this is currently required.
                // FIX THIS, this is a hotfix
                var b = GetBroadphase(ev.Body.Owner, xform, GetEntityQuery<BroadphaseComponent>(), xformQuery);
                if (b != null)
                    RemoveBroadTree(ev.Body, b, ev.Body.BodyType);

                return;
            }

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var broadphase = GetBroadphase(ev.Body.Owner, xform, broadQuery, xformQuery);

            if (broadphase == null)
                return;

            if (ev.CanCollide)
            {
                RemoveSundriesTree(ev.Body.Owner, broadphase, ev.Body.BodyType);
                AddBroadTree(ev.Body, broadphase, ev.Body.BodyType, xform: xform);
            }
            else
            {
                RemoveBroadTree(ev.Body, broadphase, ev.Body.BodyType);
                AddSundriesTree(ev.Body.Owner, broadphase, ev.Body.BodyType);
            }
        }

        private void OnBodyTypeChange(EntityUid uid, PhysicsComponent component, ref PhysicsBodyTypeChangedEvent args)
        {
            // only matters if we swapped from static to non-static.
            if (args.Old != BodyType.Static && args.New != BodyType.Static)
                return;

            var xformQuery = GetEntityQuery<TransformComponent>();
            var xform = xformQuery.GetComponent(uid);

            if (xform.GridUid == uid)
                return;

            // fun fact: container insertion tries to update the fucking lookups like 3 or more times, each time iterating through all of its parents.
            if (_container.IsEntityOrParentInContainer(uid, null, xform, null, xformQuery))
                return;

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var broadphase = GetBroadphase(uid, xform, broadQuery, xformQuery);

            if (broadphase == null)
                return;

            if (component.CanCollide)
            {
                RemoveBroadTree(component, broadphase, args.Old);
                AddBroadTree(component, broadphase, component.BodyType);
            }
            else
            {
                RemoveSundriesTree(uid, broadphase, args.Old);
                AddSundriesTree(uid, broadphase, component.BodyType);
            }    
        }

        private void RemoveBroadTree(PhysicsComponent body, BroadphaseComponent lookup, BodyType bodyType, FixturesComponent? manager = null)
        {
            if (!Resolve(body.Owner, ref manager))
                return;

            if (!TryComp<TransformComponent>(lookup.Owner, out var lookupXform) || lookupXform.MapUid == null)
            {
                throw new InvalidOperationException();
            }

            var tree = bodyType == BodyType.Static ? lookup.StaticTree : lookup.DynamicTree;
            var moveBuffer = Comp<SharedPhysicsMapComponent>(lookupXform.MapUid.Value).MoveBuffer;

            foreach (var (_, fixture) in manager.Fixtures)
            {
                DestroyProxies(fixture, tree, moveBuffer);
            }
        }

        private void DestroyProxies(Fixture fixture, IBroadPhase tree, Dictionary<FixtureProxy, Box2> moveBuffer)
        {
            for (var i = 0; i < fixture.ProxyCount; i++)
            {
                var proxy = fixture.Proxies[i];
                tree.RemoveProxy(proxy.ProxyId);
                moveBuffer.Remove(proxy);
            }

            fixture.ProxyCount = 0;
            fixture.Proxies = Array.Empty<FixtureProxy>();
        }

        private void AddBroadTree(PhysicsComponent body, BroadphaseComponent lookup, BodyType bodyType, FixturesComponent? manager = null, TransformComponent? xform = null)
        {
            if (!Resolve(body.Owner, ref manager, ref xform))
                return;

            var tree = bodyType == BodyType.Static ? lookup.StaticTree : lookup.DynamicTree;
            var xformQuery = GetEntityQuery<TransformComponent>();

            DebugTools.Assert(!_container.IsEntityOrParentInContainer(body.Owner, null, xform, null, xformQuery));

            if (!TryComp<TransformComponent>(lookup.Owner, out var lookupXform) || lookupXform.MapUid == null)
            {
                throw new InvalidOperationException();
            }

            var (worldPos, worldRot) = _transform.GetWorldPositionRotation(xform, xformQuery);
            var mapTransform = new Transform(worldPos, worldRot);
            var (_, broadWorldRot, _, broadInvMatrix) = xformQuery.GetComponent(lookup.Owner).GetWorldPositionRotationMatrixWithInv();
            var broadphaseTransform = new Transform(broadInvMatrix.Transform(mapTransform.Position), mapTransform.Quaternion2D.Angle - broadWorldRot);
            var moveBuffer = Comp<SharedPhysicsMapComponent>(lookupXform.MapUid.Value).MoveBuffer;

            foreach (var (_, fixture) in manager.Fixtures)
            {
                AddOrMoveProxies(fixture, tree, broadphaseTransform, mapTransform, moveBuffer);
            }
        }

        private void AddOrMoveProxies(
            Fixture fixture,
            IBroadPhase tree,
            Transform broadphaseTransform,
            Transform mapTransform,
            Dictionary<FixtureProxy, Box2> moveBuffer)
        {
            DebugTools.Assert(fixture.Body.CanCollide);

            // Moving
            if (fixture.ProxyCount > 0)
            {
                for (var i = 0; i < fixture.ProxyCount; i++)
                {
                    var bounds = fixture.Shape.ComputeAABB(broadphaseTransform, i);
                    var proxy = fixture.Proxies[i];
                    tree.MoveProxy(proxy.ProxyId, bounds, Vector2.Zero);
                    proxy.AABB = bounds;
                    moveBuffer[proxy] = fixture.Shape.ComputeAABB(mapTransform, i);
                }

                return;
            }

            var count = fixture.Shape.ChildCount;
            var proxies = new FixtureProxy[count];

            for (var i = 0; i < count; i++)
            {
                var bounds = fixture.Shape.ComputeAABB(broadphaseTransform, i);
                var proxy = new FixtureProxy(bounds, fixture, i);
                proxy.ProxyId = tree.AddProxy(ref proxy);
                proxy.AABB = bounds;
                proxies[i] = proxy;
                moveBuffer[proxy] = fixture.Shape.ComputeAABB(mapTransform, i);
            }

            fixture.Proxies = proxies;
            fixture.ProxyCount = count;
        }

        private void AddSundriesTree(EntityUid uid, BroadphaseComponent lookup, BodyType bodyType)
        {
            DebugTools.Assert(!_container.IsEntityOrParentInContainer(uid));
            var tree = bodyType == BodyType.Static ? lookup.StaticSundriesTree : lookup.SundriesTree;
            tree.Add(uid);
        }

        private void RemoveSundriesTree(EntityUid uid, BroadphaseComponent lookup, BodyType bodyType)
        {
            var tree = bodyType == BodyType.Static ? lookup.StaticSundriesTree : lookup.SundriesTree;
            tree.Remove(uid);
        }

        private void OnEntityInit(EntityUid uid)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();

            if (!xformQuery.TryGetComponent(uid, out var xform))
            {
                return;
            }

            if (_container.IsEntityOrParentInContainer(uid, null, xform, null, xformQuery))
                return;

            if (_mapManager.IsMap(uid) ||
                _mapManager.IsGrid(uid))
            {
                return;
            }

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var lookup = GetBroadphase(uid, xform, broadQuery, xformQuery);

            // If nullspace or the likes.
            if (lookup == null) return;

            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            DebugTools.Assert(coordinates.EntityId == lookup.Owner);
            var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);

            // If we're contained then LocalRotation should be 0 anyway.
            var aabb = GetAABBNoContainer(uid, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation);

            // Any child entities should be handled by their own OnEntityInit
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation, false);
        }

        private void OnMove(ref MoveEvent args)
        {
            // Even if the entity is contained it may have children that aren't so we still need to update.
            if (!CanMoveUpdate(args.Sender))
                return;

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            var lookup = GetBroadphase(args.Sender, args.Component, broadQuery, xformQuery);

            if (lookup == null) return;

            var xform = args.Component;
            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            var lookupRotation = _transform.GetWorldRotation(lookup.Owner, xformQuery);
            var aabb = GetAABBNoContainer(args.Sender, coordinates.Position, _transform.GetWorldRotation(xform) - lookupRotation);
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation);
        }

        private bool CanMoveUpdate(EntityUid uid)
        {
            return !_mapManager.IsMap(uid) &&
                     !_mapManager.IsGrid(uid) &&
                     !_container.IsEntityInContainer(uid);
        }

        private void OnParentChange(ref EntParentChangedMessage args)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            var metaQuery = GetEntityQuery<MetaDataComponent>();
            var meta = metaQuery.GetComponent(args.Entity);
            var xform = args.Transform;

            // If our parent is changing due to a container-insert, we let the container insert event handle that. Note
            // that the in-container flag gets set BEFORE insert parent change, and gets unset before the container
            // removal parent-change. So if it is set here, this must mean we are getting inserted.
            //
            // However, this means that this method will still get run in full on container removal. Additionally,
            // because not all container removals are guaranteed to result in a parent change, container removal events
            // also need to add the entity to a tree. So this generally results in:
            // add-to-tree -> remove-from-tree -> add-to-tree.
            // Though usually, `oldLookup == newLookup` for the last step. Its still shit though.
            //
            // TODO IMPROVE CONTAINER REMOVAL HANDLING

            if (_container.IsEntityOrParentInContainer(args.Entity, meta, xform, metaQuery, xformQuery))
                return;

            if (meta.EntityLifeStage < EntityLifeStage.Initialized ||
                _mapManager.IsGrid(args.Entity) ||
                _mapManager.IsMap(args.Entity))
            {
                return;
            }

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            BroadphaseComponent? oldLookup = null;

            if (args.OldMapId != MapId.Nullspace && xformQuery.TryGetComponent(args.OldParent, out var parentXform))
            {
                // If the old parent has a broadphase return that, otherwise return the parent's broadphase.
                if (!broadQuery.TryGetComponent(args.OldParent.Value, out oldLookup))
                {
                    oldLookup = GetBroadphase(args.OldParent.Value, parentXform, broadQuery, xformQuery);
                }
            }

            var newLookup = GetBroadphase(args.Entity, xform, broadQuery, xformQuery);

            // If parent is the same then no need to do anything as position should stay the same.
            if (oldLookup == newLookup) return;

            RemoveFromEntityTree(oldLookup, xform, xformQuery);

            if (newLookup != null)
                AddToEntityTree(newLookup, xform, xformQuery, _transform.GetWorldRotation(newLookup.Owner, xformQuery));
        }

        private void OnContainerRemove(EntRemovedFromContainerMessage ev)
        {
            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            var xform = xformQuery.GetComponent(ev.Entity);
            var lookup = GetBroadphase(ev.Entity, xform, broadQuery, xformQuery);

            if (lookup == null) return;

            AddToEntityTree(lookup, xform, xformQuery, _transform.GetWorldRotation(lookup.Owner, xformQuery));
        }

        private void OnContainerInsert(EntInsertedIntoContainerMessage ev)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            BroadphaseComponent? lookup;

            if (ev.OldParent == EntityUid.Invalid)
                return;

            if (!broadQuery.TryGetComponent(ev.OldParent, out lookup))
            {
                if (!xformQuery.TryGetComponent(ev.OldParent, out var parentXform))
                    return;

                lookup = GetBroadphase(ev.OldParent, parentXform, broadQuery, xformQuery);
            }

            RemoveFromEntityTree(lookup, xformQuery.GetComponent(ev.Entity), xformQuery);
        }

        private void AddToEntityTree(
            BroadphaseComponent lookup,
            TransformComponent xform,
            EntityQuery<TransformComponent> xformQuery,
            Angle lookupRotation,
            bool recursive = true)
        {
            var coordinates = _transform.GetMoverCoordinates(xform.Coordinates, xformQuery);
            // If we're contained then LocalRotation should be 0 anyway.
            var aabb = GetAABBNoContainer(xform.Owner, coordinates.Position, _transform.GetWorldRotation(xform, xformQuery) - lookupRotation);
            AddToEntityTree(lookup, xform, aabb, xformQuery, lookupRotation, recursive);
        }

        private void AddToEntityTree(
            BroadphaseComponent? lookup,
            TransformComponent xform,
            Box2 aabb,
            EntityQuery<TransformComponent> xformQuery,
            Angle lookupRotation,
            bool recursive = true)
        {
            // If entity is in nullspace then no point keeping track of data structure.
            if (lookup == null) return;

            AddTree(xform.Owner, lookup, aabb, xform: xform);

            var childEnumerator = xform.ChildEnumerator;

            if (xform.ChildCount == 0 || !recursive) return;

            // If they're in a container then don't add to entitylookup due to the additional cost.
            // It's cheaper to just query these components at runtime given PVS no longer uses EntityLookupSystem.
            if (EntityManager.TryGetComponent<ContainerManagerComponent>(xform.Owner, out var conManager))
            {
                while (childEnumerator.MoveNext(out var child))
                {
                    if (conManager.ContainsEntity(child.Value)) continue;

                    var childXform = xformQuery.GetComponent(child.Value);
                    var coordinates = _transform.GetMoverCoordinates(childXform.Coordinates, xformQuery);
                    // TODO: If we have 0 position and not contained can optimise these further, but future problem.
                    var childAABB = GetAABBNoContainer(child.Value, coordinates.Position, childXform.WorldRotation - lookupRotation);
                    AddToEntityTree(lookup, childXform, childAABB, xformQuery, lookupRotation);
                }
            }
            else
            {
                while (childEnumerator.MoveNext(out var child))
                {
                    var childXform = xformQuery.GetComponent(child.Value);
                    var coordinates = _transform.GetMoverCoordinates(childXform.Coordinates, xformQuery);
                    // TODO: If we have 0 position and not contained can optimise these further, but future problem.
                    var childAABB = GetAABBNoContainer(child.Value, coordinates.Position, childXform.WorldRotation - lookupRotation);
                    AddToEntityTree(lookup, childXform, childAABB, xformQuery, lookupRotation);
                }
            }
        }

        private void AddTree(EntityUid uid, BroadphaseComponent broadphase, Box2 aabb, PhysicsComponent? body = null, TransformComponent? xform = null)
        {
            if (!Resolve(uid, ref body, false) || !body.CanCollide)
            {
                if (body?.BodyType == BodyType.Static)
                    broadphase.StaticSundriesTree.AddOrUpdate(uid, aabb);
                else
                    broadphase.SundriesTree.AddOrUpdate(uid, aabb);
                return;
            }

            AddBroadTree(body, broadphase, body.BodyType, xform: xform);
        }

        private void RemoveTree(EntityUid uid, BroadphaseComponent broadphase, PhysicsComponent? body = null)
        {
            if (!Resolve(uid, ref body, false) || !body.CanCollide)
            {
                if (body?.BodyType == BodyType.Static)
                    broadphase.StaticSundriesTree.Remove(uid);
                else
                    broadphase.SundriesTree.Remove(uid);
                return;
            }

            RemoveBroadTree(body, broadphase, body.BodyType);
        }

        /// <summary>
        /// Recursively iterates through this entity's children and removes them from the entitylookupcomponent.
        /// </summary>
        private void RemoveFromEntityTree(BroadphaseComponent? lookup, TransformComponent xform, EntityQuery<TransformComponent> xformQuery, bool recursive = true)
        {
            // TODO: Move this out of the loop
            if (lookup == null) return;

            RemoveTree(xform.Owner, lookup);

            if (!recursive) return;

            var childEnumerator = xform.ChildEnumerator;

            while (childEnumerator.MoveNext(out var child))
            {
                RemoveFromEntityTree(lookup, xformQuery.GetComponent(child.Value), xformQuery);
            }
        }

        /// <summary>
        /// Attempt to get the relevant broadphase for this entity.
        /// Can return null if it's the map entity.
        /// </summary>
        private BroadphaseComponent? GetBroadphase(TransformComponent xform)
        {
            if (xform.MapID == MapId.Nullspace) return null;

            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            return GetBroadphase(xform.Owner, xform, broadQuery, xformQuery);
        }

        public BroadphaseComponent? GetBroadphase(EntityUid uid)
        {
            var broadQuery = GetEntityQuery<BroadphaseComponent>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            return GetBroadphase(uid, xformQuery.GetComponent(uid), broadQuery, xformQuery);
        }

        public BroadphaseComponent? GetBroadphase(EntityUid uid, TransformComponent xform, EntityQuery<BroadphaseComponent> broadQuery, EntityQuery<TransformComponent> xformQuery)
        {
            if (xform.MapID == MapId.Nullspace) return null;

            var parent = xform.ParentUid;

            // if it's map (or in null-space) return null. Grids should return the map's broadphase.

            while (parent.IsValid())
            {
                if (broadQuery.TryGetComponent(parent, out var comp))
                    return comp;

                parent = xformQuery.GetComponent(parent).ParentUid;
            }

            return null;
        }

        #endregion

        #region Bounds

        /// <summary>
        /// Get the AABB of an entity with the supplied position and angle. Tries to consider if the entity is in a container.
        /// </summary>
        internal Box2 GetAABB(EntityUid uid, Vector2 position, Angle angle, TransformComponent xform, EntityQuery<TransformComponent> xformQuery)
        {
            // If we're in a container then we just use the container's bounds.
            if (_container.TryGetOuterContainer(uid, xform, out var container, xformQuery))
            {
                return GetAABBNoContainer(container.Owner, position, angle);
            }

            return GetAABBNoContainer(uid, position, angle);
        }

        /// <summary>
        /// Get the AABB of an entity with the supplied position and angle without considering containers.
        /// </summary>
        private Box2 GetAABBNoContainer(EntityUid uid, Vector2 position, Angle angle)
        {
            if (TryComp<ILookupWorldBox2Component>(uid, out var worldLookup))
            {
                var transform = new Transform(position, angle);
                return worldLookup.GetAABB(transform);
            }
            else
            {
                return new Box2(position, position);
            }
        }

        public Box2 GetWorldAABB(EntityUid uid, TransformComponent? xform = null)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            xform ??= xformQuery.GetComponent(uid);
            var (worldPos, worldRot) = xform.GetWorldPositionRotation();

            return GetAABB(uid, worldPos, worldRot, xform, xformQuery);
        }

        #endregion
    }
}
