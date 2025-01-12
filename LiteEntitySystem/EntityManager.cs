using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Extensions;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate T EntityConstructor<out T>(EntityParams entityParams) where T : InternalEntity;
    
    [Flags]
    public enum ExecuteFlags : byte
    {
        ///<summary>Execute RPC for owner of entity</summary>
        SendToOwner = 1,
        
        ///<summary>Execute RPC for non owners</summary>
        SendToOther = 1 << 1,
        
        ///<summary>Execute RPC for all players</summary>
        SendToAll = SendToOwner | SendToOther,
        
        ///<summary>Execute RPC on client for owner of entity on prediction</summary>
        ExecuteOnPrediction = 1 << 2,
        
        ///<summary>Execute RPC directly on server</summary>
        ExecuteOnServer = 1 << 3,
        
        ///<summary>All flags, send to owner, to others, execute on prediction and on server</summary>
        All = SendToOther | SendToOwner | ExecuteOnPrediction | ExecuteOnServer
    }

    public enum NetworkMode
    {
        Client,
        Server
    }

    public enum UpdateMode
    {
        Normal,
        PredictionRollback
    }
    
    public enum DeserializeResult
    {
        Done,
        Error,
        HeaderCheckFailed
    }
    
    public enum MaxHistorySize : byte
    {
        Size16 = 16,
        Size32 = 32,
        Size64 = 64,
        Size128 = 128
    }

    /// <summary>
    /// Base class for client and server manager
    /// </summary>
    public abstract class EntityManager
    {
        /// <summary>
        /// Maximum synchronized (without LocalOnly) entities
        /// </summary>
        public const int MaxSyncedEntityCount = 8192;

        public const int MaxEntityCount = MaxSyncedEntityCount * 2;
        
        public const byte ServerPlayerId = 0;

        /// <summary>
        /// Invalid entity id
        /// </summary>
        public const ushort InvalidEntityId = 0;
        
        /// <summary>
        /// Total entities count (including local)
        /// </summary>
        public ushort EntitiesCount { get; private set; }

        /// <summary>
        /// Current tick
        /// </summary>
        public ushort Tick => _tick;

        /// <summary>
        /// Interpolation time between logic and render
        /// </summary>
        public float LerpFactor => _lerpFactor;
        
        /// <summary>
        /// Current update mode (can be used inside entities to separate logic for rollbacks)
        /// </summary>
        public UpdateMode UpdateMode { get; protected set; }
        
        /// <summary>
        /// Current mode (Server or Client)
        /// </summary>
        public readonly NetworkMode Mode;
        
        /// <summary>
        /// Is server
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// Is client
        /// </summary>
        public readonly bool IsClient;
        
        /// <summary>
        /// FPS of game logic
        /// </summary>
        public readonly int FramesPerSecond;
        
        /// <summary>
        /// Fixed delta time
        /// </summary>
        public readonly double DeltaTime;

        /// <summary>
        /// Fixed delta time (float for less precision)
        /// </summary>
        public readonly float DeltaTimeF;
        
        /// <summary>
        /// Size of history (in ticks) for lag compensation. Tune for your game fps 
        /// </summary>
        public MaxHistorySize MaxHistorySize = MaxHistorySize.Size32;

        /// <summary>
        /// Local player id (0 on server)
        /// </summary>
        public byte PlayerId => InternalPlayerId;

        public readonly byte HeaderByte;
        
        public bool InRollBackState => UpdateMode == UpdateMode.PredictionRollback;
        public bool InNormalState => UpdateMode == UpdateMode.Normal;
        
        protected const int MaxSavedStateDiff = 30;
        internal const int MaxParts = 256;
        private const int MaxTicksPerUpdate = 5;

        public double VisualDeltaTime { get; private set; }
        public const int MaxPlayers = byte.MaxValue-1;
        
        protected ushort _tick;
        
        protected readonly AVLTree<InternalEntity> AliveEntities = new();
        protected readonly AVLTree<EntityLogic> LagCompensatedEntities = new();
        protected readonly AVLTree<InternalEntity> AllEntities = new();

        private readonly double _stopwatchFrequency;
        private readonly Stopwatch _stopwatch = new();

        private readonly SingletonEntityLogic[] _singletonEntities;
        private readonly IEntityFilter[] _entityFilters;
        private readonly Dictionary<Type, ushort> _registeredTypeIds = new();
        private readonly Dictionary<Type, ILocalSingleton> _localSingletons = new();

        internal readonly InternalEntity[] EntitiesDict = new InternalEntity[MaxEntityCount+1];
        internal readonly EntityClassData[] ClassDataDict;

        private readonly long _deltaTimeTicks;
        private readonly long _slowdownTicks;
        private long _accumulator;
        private long _lastTime;
        private bool _lagCompensationEnabled;
        private float _lerpFactor;

        internal byte InternalPlayerId;
        protected readonly InputProcessor InputProcessor;
        protected float SpeedMultiplier;

        protected const float TimeSpeedChangeCoef = 0.1f;

        /// <summary>
        /// Is entity manager running
        /// IsRunning - true after first update
        /// IsRunning - sets to false after Reset() call
        /// </summary>
        public bool IsRunning => _stopwatch.IsRunning;

        /// <summary>
        /// Register custom field type with interpolation
        /// </summary>
        /// <param name="interpolationDelegate">interpolation function</param>
        public static void RegisterFieldType<T>(InterpolatorDelegateWithReturn<T> interpolationDelegate) where T : unmanaged =>
            ValueTypeProcessor.Registered[typeof(T)] = new UserTypeProcessor<T>(interpolationDelegate);
        
        /// <summary>
        /// Register custom field type
        /// </summary>
        public static void RegisterFieldType<T>() where T : unmanaged =>
            ValueTypeProcessor.Registered[typeof(T)] = new UserTypeProcessor<T>(null);
        
        private static void RegisterBasicFieldType<T>(ValueTypeProcessor<T> proc) where T : unmanaged =>
            ValueTypeProcessor.Registered.Add(typeof(T), proc);

        static EntityManager()
        {
#if UNITY_ANDROID
            if (IntPtr.Size == 4)
                K4os.Compression.LZ4.LZ4Codec.Enforce32 = true;
#endif
            RegisterBasicFieldType(new ValueTypeProcessor<byte>());
            RegisterBasicFieldType(new ValueTypeProcessor<sbyte>());
            RegisterBasicFieldType(new ValueTypeProcessor<short>());
            RegisterBasicFieldType(new ValueTypeProcessor<ushort>());
            RegisterBasicFieldType(new ValueTypeProcessorInt());
            RegisterBasicFieldType(new ValueTypeProcessor<uint>());
            RegisterBasicFieldType(new ValueTypeProcessorLong());
            RegisterBasicFieldType(new ValueTypeProcessor<ulong>());
            RegisterBasicFieldType(new ValueTypeProcessorFloat());
            RegisterBasicFieldType(new ValueTypeProcessorDouble());
            RegisterBasicFieldType(new ValueTypeProcessor<bool>());
            RegisterBasicFieldType(new ValueTypeProcessor<EntitySharedReference>());
            RegisterFieldType<FloatAngle>(FloatAngle.Lerp);
        }

        protected EntityManager(EntityTypesMap typesMap, InputProcessor inputProcessor, NetworkMode mode, byte framesPerSecond, byte headerByte)
        {
            HeaderByte = headerByte;
            ClassDataDict = new EntityClassData[typesMap.MaxId+1];

            ushort filterCount = 0;
            ushort singletonCount = 0;
            
            //preregister some types
            _registeredTypeIds.Add(typeof(ControllerLogic), filterCount++);
            
            foreach (var (entType, typeInfo) in typesMap.RegisteredTypes)
            {
                var classData = new EntityClassData(
                    entType.IsSubclassOf(typeof(SingletonEntityLogic)) ? singletonCount++ : filterCount++, 
                    entType, 
                    typeInfo);
                _registeredTypeIds.Add(entType, classData.FilterId);
                ClassDataDict[typeInfo.ClassId] = classData;
                //Logger.Log($"Register: {entType.Name} ClassId: {classId}");
            }
            
            foreach (var registeredType in typesMap.RegisteredTypes.Values)
            {
                //map base ids
                ClassDataDict[registeredType.ClassId].PrepareBaseTypes(_registeredTypeIds, ref singletonCount, ref filterCount);
            }

            _entityFilters = new IEntityFilter[filterCount];
            _singletonEntities = new SingletonEntityLogic[singletonCount];

            InputProcessor = inputProcessor;
            
            Mode = mode;
            IsServer = Mode == NetworkMode.Server;
            IsClient = Mode == NetworkMode.Client;
            FramesPerSecond = framesPerSecond;
            DeltaTime = 1.0 / framesPerSecond;
            DeltaTimeF = (float) DeltaTime;
            _stopwatchFrequency = 1.0 / Stopwatch.Frequency;
            _deltaTimeTicks = (long)(DeltaTime * Stopwatch.Frequency);
            _slowdownTicks = (long)(DeltaTime * TimeSpeedChangeCoef * Stopwatch.Frequency);
            if (_slowdownTicks < 100)
                _slowdownTicks = 100;
        }

        /// <summary>
        /// Remove all entities and reset all counters and timers
        /// </summary>
        public virtual void Reset()
        {
            foreach (var localSingleton in _localSingletons)
                localSingleton.Value?.Destroy();
            
            _localSingletons.Clear();
            EntitiesCount = 0;
            
            _tick = 0;
            VisualDeltaTime = 0.0;
            _accumulator = 0;
            _lastTime = 0;
            InternalPlayerId = 0;
            _stopwatch.Stop();
            _stopwatch.Reset();
            AliveEntities.Clear();
            
            foreach (var entity in AllEntities)
                entity.DestroyInternal();
            AllEntities.Clear();
            Array.Clear(EntitiesDict, 0, EntitiesDict.Length);
            Array.Clear(_entityFilters, 0, _entityFilters.Length);
        }

        /// <summary>
        /// Get entity by id
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <returns>Entity if it exists, null if id == InvalidEntityId or entity is another type or version</returns>
        public T GetEntityById<T>(EntitySharedReference id) where T : InternalEntity =>
             id.Id != InvalidEntityId
                ? EntitiesDict[id.Id] is T entity && entity.Version == id.Version ? entity : null
                : null;
        
        /// <summary>
        /// Try get entity by id
        /// throws exception if entity is null or invalid type
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <param name="entity">out entity if exists otherwise null</param>
        /// <returns>true if it exists, false if id == InvalidEntityId or entity is another type or version</returns>
        public bool TryGetEntityById<T>(EntitySharedReference id, out T entity) where T : InternalEntity =>
            (entity = id.Id != InvalidEntityId
                ? EntitiesDict[id.Id] is T castedEnt && castedEnt.Version == id.Version ? castedEnt : null
                : null) != null;
        
        private EntityFilter<T> GetEntitiesInternal<T>() where T : InternalEntity
        {
            if (!_registeredTypeIds.TryGetValue(typeof(T), out ushort typeId))
                throw new Exception($"Unregistered type: {typeof(T)}");
            
            ref var entityFilter = ref _entityFilters[typeId];
            EntityFilter<T> typedFilter;
            if (entityFilter != null)
            {
                typedFilter = (EntityFilter<T>)entityFilter;
                return typedFilter;
            }
            typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            foreach (var entity in AllEntities)
            {
                if(entity is T castedEnt)
                    typedFilter.Add(castedEnt);
            }
            return typedFilter;
        }

        /// <summary>
        /// Get all entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetEntities<T>() where T : EntityLogic => GetEntitiesInternal<T>();
        
        /// <summary>
        /// Get all controller entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetControllers<T>() where T : ControllerLogic => GetEntitiesInternal<T>();

        /// <summary>
        /// Get existing singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity, can throw exceptions on invalid type</returns>
        public T GetSingleton<T>() where T : SingletonEntityLogic =>
            _registeredTypeIds.TryGetValue(typeof(T), out ushort registeredId) ? _singletonEntities[registeredId] as T : null;

        /// <summary>
        /// Is singleton exists and has correct type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public bool HasSingleton<T>() where T : SingletonEntityLogic =>
            _singletonEntities[_registeredTypeIds[typeof(T)]] is T;

        /// <summary>
        /// Add local (not synchronized) singleton.
        /// </summary>
        /// <param name="singleton">Signleton to add</param>
        public void AddLocalSingleton<T>(T singleton) where T : ILocalSingleton =>
            _localSingletons[typeof(T)] = singleton;
        
        /// <summary>
        /// Get local (not synchronized) singleton.
        /// </summary>
        public T GetLocalSingleton<T>() where T : ILocalSingleton =>
            _localSingletons.TryGetValue(typeof(T), out var singleton) && singleton is T typedSingleton ? typedSingleton : default;

        /// <summary>
        /// TryGet local (not synchronized) singleton.
        /// </summary>
        public bool TryGetLocalSingleton<T>(out T result) where T : ILocalSingleton
        {
            if (_localSingletons.TryGetValue(typeof(T), out var singleton) && singleton is T typedSingleton)
            {
                result = typedSingleton;
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Try get singleton entity
        /// </summary>
        /// <param name="singleton">result singleton entity</param>
        /// <typeparam name="T">Singleton type</typeparam>
        /// <returns>true if entity exists</returns>
        public bool TryGetSingleton<T>(out T singleton) where T : SingletonEntityLogic
        {
            if (!_registeredTypeIds.TryGetValue(typeof(T), out ushort registeredId))
            {
                singleton = null;
                return false;
            }
            var s = _singletonEntities[registeredId];
            if (s != null)
            {
                singleton = (T)s;
                return true;
            }
            singleton = null;
            return false;
        }

        protected InternalEntity AddEntity(EntityParams entityParams)
        {
            var entityHeader = entityParams.Header;
            if (entityHeader.Id == InvalidEntityId || entityHeader.Id >= EntitiesDict.Length)
            {
                throw new ArgumentException($"Invalid entity id: {entityHeader.Id}");
            }

            if (entityHeader.ClassId >= ClassDataDict.Length)
            {
                throw new Exception($"Unregistered entity class: {entityHeader.ClassId}");
            }
            
            var classData = ClassDataDict[entityHeader.ClassId];
            if (classData.EntityConstructor == null)
            {
                throw new Exception($"Unregistered entity class: {entityHeader.ClassId}");
            }
            var entity = classData.EntityConstructor(entityParams);
            entity.RegisterRpcInternal();
            
            if(entity.Id < EntitiesDict.Length)
                EntitiesDict[entity.Id] = entity;
            EntitiesCount++;
            return entity;
        }

        protected void ConstructEntity(InternalEntity e)
        {
            ref var classData = ref ClassDataDict[e.ClassId];
            if (classData.IsSingleton)
            {
                _singletonEntities[classData.FilterId] = (SingletonEntityLogic)e;
                foreach (int baseId in classData.BaseIds)
                    _singletonEntities[baseId] = (SingletonEntityLogic)e;
            }

            e.OnConstructed();

            if (!classData.IsSingleton)
            {
                _entityFilters[classData.FilterId]?.Add(e);
                foreach (int baseId in classData.BaseIds)
                    _entityFilters[baseId]?.Add(e);
            }
            
            AllEntities.Add(e);
            if (IsEntityAlive(classData.Flags, e))
            {
                AliveEntities.Add(e);
                OnAliveEntityAdded(e);
            }
            if (IsEntityLagCompensated(e))
                LagCompensatedEntities.Add((EntityLogic)e);
        }
        
        protected virtual void OnAliveEntityAdded(InternalEntity e)
        {
            
        }

        private static bool IsEntityLagCompensated(InternalEntity e)
            => !e.IsLocal && e is EntityLogic && e.ClassData.LagCompensatedCount > 0;

        private bool IsEntityAlive(EntityFlags flags, InternalEntity entity)
            => flags.HasFlagFast(EntityFlags.Updateable) && (IsServer || entity.IsLocal || (IsClient && flags.HasFlagFast(EntityFlags.UpdateOnClient)));

        internal virtual void OnEntityDestroyed(InternalEntity e)
        {
            ref var classData = ref ClassDataDict[e.ClassId];
            if (classData.IsSingleton)
            {
                _singletonEntities[classData.FilterId] = null;
                foreach (int baseId in classData.BaseIds) 
                    _singletonEntities[baseId] = null;
            }
            else
            {
                _entityFilters[classData.FilterId]?.Remove(e);
                foreach (int baseId in classData.BaseIds) 
                    _entityFilters[baseId]?.Remove(e);
            }
            if(IsEntityLagCompensated(e))
                LagCompensatedEntities.Remove((EntityLogic)e);
            if (IsEntityAlive(classData.Flags, e))
                AliveEntities.Remove(e);
        }
        
        protected void RemoveEntity(InternalEntity e)
        {
            if(!e.IsDestroyed)
                Logger.LogError($"Remove not destroyed entity!: {e}");
            AllEntities.Remove(e);
            if(e.Id < EntitiesDict.Length)
                EntitiesDict[e.Id] = null;
            EntitiesCount--;
            ClassDataDict[e.ClassId].ReleaseDataCache(e);
            //Logger.Log($"{Mode} - RemoveEntity: {e.Id}");
        }
        
        public void EnableLagCompensation(NetPlayer player)
        {
            if (_lagCompensationEnabled || (IsClient && InNormalState))
                return;

            _lagCompensationEnabled = true;
            //Logger.Log($"C: {IsClient} compensated: {player.SimulatedServerTick} =====");
            foreach (var entity in LagCompensatedEntities)
            {
                entity.EnableLagCompensation(player);
                //entity.DebugPrint();
            }
        }

        public void DisableLagCompensation()
        {
            if(!_lagCompensationEnabled)
                return;
            _lagCompensationEnabled = false;
            //Logger.Log($"restored: {Tick} =====");
            foreach (var entity in LagCompensatedEntities)
            {
                entity.DisableLagCompensation();
                //entity.DebugPrint();
            }
        }

        protected abstract void OnLogicTick();

        internal abstract void EntityFieldChanged<T>(InternalEntity entity, ushort fieldId, ref T newValue)
            where T : unmanaged;

        /// <summary>
        /// Main update method, updates internal fixed timer and do all other stuff
        /// </summary>
        public virtual void Update()
        {
            if(!_stopwatch.IsRunning)
                _stopwatch.Start();

            long elapsedTicks = _stopwatch.ElapsedTicks;
            long ticksDelta = elapsedTicks - _lastTime;
            VisualDeltaTime = ticksDelta * _stopwatchFrequency;
            
            foreach (var localSingleton in _localSingletons)
                if(localSingleton.Value is ILocalSingletonWithUpdate updSingleton)
                    updSingleton.VisualUpdate((float)VisualDeltaTime);
            
            _accumulator += ticksDelta;
            _lastTime = elapsedTicks;
            long maxTicks = (long)(_deltaTimeTicks + SpeedMultiplier * _slowdownTicks);

            int updates = 0;
            while (_accumulator >= maxTicks)
            {
                //Lag
                if (updates >= MaxTicksPerUpdate)
                {
                    _lastTime = _stopwatch.ElapsedTicks;
                    _accumulator = 0;
                    return;
                }

                foreach (var localSingleton in _localSingletons)
                    if(localSingleton.Value is ILocalSingletonWithUpdate updSingleton)
                        updSingleton.Update(DeltaTimeF);
                OnLogicTick();
                _tick++;

                _accumulator -= maxTicks;
                updates++;
            }
            _lerpFactor = (float)_accumulator / maxTicks;
        }
    }
}
