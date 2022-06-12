﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Chroma.Settings;
using CustomJSONData.CustomBeatmap;
using HarmonyLib;
using Heck.Animation;
using IPA.Utilities;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;
using static Chroma.ChromaController;
using static Heck.HeckController;
using Logger = IPA.Logging.Logger;
using Object = UnityEngine.Object;

namespace Chroma.Lighting.EnvironmentEnhancement
{
    // ReSharper disable UnusedMember.Global
    internal enum LookupMethod
    {
        Regex,
        Exact,
        Contains,
        StartsWith,
        EndsWith
    }

    [UsedImplicitly]
    internal class EnvironmentEnhancementManager : IDisposable
    {
        private const string LOOKUPDLL = @"LookupID.dll";

        private static readonly FieldAccessor<TrackLaneRing, Vector3>.Accessor _positionOffsetAccessor = FieldAccessor<TrackLaneRing, Vector3>.GetAccessor("_positionOffset");
        private static readonly FieldAccessor<TrackLaneRing, float>.Accessor _rotZAccessor = FieldAccessor<TrackLaneRing, float>.GetAccessor("_rotZ");
        private static readonly FieldAccessor<TrackLaneRing, float>.Accessor _posZAccessor = FieldAccessor<TrackLaneRing, float>.GetAccessor("_posZ");

        private readonly List<GameObjectInfo> _gameObjectInfos = new();

        private readonly CustomBeatmapData _beatmapData;
        private readonly float _noteLinesDistance;
        private readonly Dictionary<string, Track> _tracks;
        private readonly ParametricBoxControllerParameters _parametricBoxControllerParameters;
        private readonly LazyInject<ComponentInitializer> _componentInitializer;
        private readonly GameObjectTrackController.Factory _trackControllerFactory;

        private readonly HashSet<GameObjectTrackController> _gameObjectTrackControllers = new();

        private EnvironmentEnhancementManager(
            BeatmapObjectSpawnController spawnController,
            IReadonlyBeatmapData beatmapData,
            Dictionary<string, Track> tracks,
            ParametricBoxControllerParameters parametricBoxControllerParameters,
            LazyInject<ComponentInitializer> componentInitializer,
            GameObjectTrackController.Factory trackControllerFactory)
        {
            if (beatmapData is not CustomBeatmapData customBeatmapData)
            {
                throw new ArgumentNullException(nameof(beatmapData));
            }

            _beatmapData = customBeatmapData;
            _noteLinesDistance = spawnController.noteLinesDistance;
            _tracks = tracks;
            _parametricBoxControllerParameters = parametricBoxControllerParameters;
            _componentInitializer = componentInitializer;
            _trackControllerFactory = trackControllerFactory;
            spawnController.StartCoroutine(DelayedStart());
        }

        internal Dictionary<TrackLaneRing, Quaternion> RingRotationOffsets { get; } = new();

        internal Dictionary<BeatmapObjectsAvoidance, Vector3> AvoidancePosition { get; } = new();

        internal Dictionary<BeatmapObjectsAvoidance, Quaternion> AvoidanceRotation { get; } = new();

        public void Dispose()
        {
            _gameObjectTrackControllers.Do(Object.Destroy);
        }

        internal enum GeometryType
        {
            SPHERE,
            CAPSULE,
            CYLINDER,
            CUBE,
            PLANE,
            QUAD,
        }

        internal readonly struct SpawnData
        {
            public readonly Vector3? Scale;
            public readonly Vector3? Position;
            public readonly Vector3? Rotation;
            public readonly Vector3? LocalPosition;
            public readonly Vector3? LocalRotation;

            public SpawnData(CustomData gameObjectData, bool v2, float _noteLinesDistance)
            {
                Scale = gameObjectData.GetVector3(v2 ? V2_SCALE : SCALE);
                Position = gameObjectData.GetVector3(v2 ? V2_POSITION : POSITION);
                Rotation = gameObjectData.GetVector3(v2 ? V2_ROTATION : ROTATION);
                LocalPosition = gameObjectData.GetVector3(v2 ? V2_LOCAL_POSITION : LOCAL_POSITION);
                LocalRotation = gameObjectData.GetVector3(v2 ? V2_LOCAL_ROTATION : LOCAL_ROTATION);

                if (!v2)
                {
                    return;
                }

                // ReSharper disable once UseNullPropagation
                if (Position.HasValue)
                {
                    Position = Position.Value * _noteLinesDistance;
                }

                // ReSharper disable once UseNullPropagation
                if (LocalPosition.HasValue)
                {
                    LocalPosition = LocalPosition.Value * _noteLinesDistance;
                }
            }

            public void TransformObject(Transform transform)
            {
                if (Scale.HasValue)
                {
                    transform.localScale = Scale.Value;
                }

                if (Position.HasValue)
                {
                    transform.position = Position.Value;
                }

                if (Rotation.HasValue)
                {
                    transform.eulerAngles = Rotation.Value;
                }

                if (LocalPosition.HasValue)
                {
                    transform.localPosition = LocalPosition.Value;
                }

                if (LocalRotation.HasValue)
                {
                    transform.localEulerAngles = LocalRotation.Value;
                }
            }
        }

        internal IEnumerator DelayedStart()
        {
            yield return new WaitForEndOfFrame();

            bool v2 = _beatmapData.version2_6_0AndEarlier;

            IEnumerable<CustomData>? environmentData = _beatmapData.customData
                .Get<List<object>>(v2 ? V2_ENVIRONMENT : ENVIRONMENT)?
                .Cast<CustomData>();
            GetAllGameObjects();

            if (environmentData != null)
            {
                if (ChromaConfig.Instance.PrintEnvironmentEnhancementDebug)
                {
                    Log.Logger.Log("=====================================");
                }

                string[] gameObjectInfoIds = _gameObjectInfos.Select(n => n.FullID).ToArray();

                foreach (CustomData gameObjectData in environmentData)
                {
                    string id = gameObjectData.Get<string>(v2 ? V2_GAMEOBJECT_ID : GAMEOBJECT_ID)
                                ?? throw new InvalidOperationException("Id was not defined.");

                    LookupMethod lookupMethod = gameObjectData.GetStringToEnum<LookupMethod?>(v2 ? V2_LOOKUP_METHOD : LOOKUP_METHOD)
                                                ?? throw new InvalidOperationException("Lookup method was not defined.");

                    int? dupeAmount = gameObjectData.Get<int?>(v2 ? V2_DUPLICATION_AMOUNT : DUPLICATION_AMOUNT);

                    bool? active = gameObjectData.Get<bool?>(v2 ? V2_ACTIVE : ACTIVE);

                    SpawnData spawnData = new(gameObjectData, v2, _noteLinesDistance);

                    int? lightID = gameObjectData.Get<int?>(v2 ? V2_LIGHT_ID : LIGHT_ID);

                    List<GameObjectInfo> foundObjects = LookupID(gameObjectInfoIds, id, lookupMethod);
                    if (foundObjects.Count > 0)
                    {
                        if (ChromaConfig.Instance.PrintEnvironmentEnhancementDebug)
                        {
                            Log.Logger.Log($"ID [\"{id}\"] using method [{lookupMethod:G}] found:");
                            foundObjects.ForEach(n => Log.Logger.Log(n.FullID));
                        }
                    }
                    else
                    {
                        Log.Logger.Log($"ID [\"{id}\"] using method [{lookupMethod:G}] found nothing.", Logger.Level.Error);
                    }

                    List<GameObjectInfo> gameObjectInfos;

                    if (dupeAmount.HasValue)
                    {
                        gameObjectInfos = new List<GameObjectInfo>();
                        foreach (GameObjectInfo gameObjectInfo in foundObjects)
                        {
                            if (ChromaConfig.Instance.PrintEnvironmentEnhancementDebug)
                            {
                                Log.Logger.Log($"Duplicating [{gameObjectInfo.FullID}]:");
                            }

                            GameObject gameObject = gameObjectInfo.GameObject;
                            Transform parent = gameObject.transform.parent;
                            Scene scene = gameObject.scene;

                            for (int i = 0; i < dupeAmount.Value; i++)
                            {
                                List<IComponentData> componentDatas = new();
                                _componentInitializer.Value.PrefillComponentsData(gameObject.transform, componentDatas);
                                GameObject newGameObject = Object.Instantiate(gameObject);
                                _componentInitializer.Value.PostfillComponentsData(newGameObject.transform, gameObject.transform, componentDatas);
                                SceneManager.MoveGameObjectToScene(newGameObject, scene);

                                // ReSharper disable once Unity.InstantiateWithoutParent
                                // need to move shit to right scene first
                                newGameObject.transform.SetParent(parent, true);
                                _componentInitializer.Value.InitializeComponents(newGameObject.transform, gameObject.transform, _gameObjectInfos, componentDatas, lightID);

                                List<GameObjectInfo> gameObjects = _gameObjectInfos.Where(n => n.GameObject == newGameObject).ToList();
                                gameObjectInfos.AddRange(gameObjects);

                                if (ChromaConfig.Instance.PrintEnvironmentEnhancementDebug)
                                {
                                    gameObjects.ForEach(n => Log.Logger.Log(n.FullID));
                                }
                            }
                        }

                        // Update array with new duplicated objects
                        gameObjectInfoIds = _gameObjectInfos.Select(n => n.FullID).ToArray();
                    }
                    else
                    {
                        if (lightID.HasValue)
                        {
                            Log.Logger.Log("LightID requested but no duplicated object to apply to.", Logger.Level.Error);
                        }

                        gameObjectInfos = foundObjects;
                    }

                    Vector3? scale = spawnData.Scale;
                    Vector3? position = spawnData.Position;
                    Vector3? localPosition = spawnData.LocalPosition;
                    Vector3? rotation = spawnData.Rotation;
                    Vector3? localRotation = spawnData.LocalRotation;

                    foreach (GameObjectInfo gameObjectInfo in gameObjectInfos)
                    {
                        GameObject gameObject = gameObjectInfo.GameObject;

                        if (active.HasValue)
                        {
                            gameObjectInfo.GameObject.SetActive(active.Value);
                        }

                        Transform transform = gameObject.transform;

                        spawnData.TransformObject(transform);

                        // Handle TrackLaneRing
                        TrackLaneRing trackLaneRing = gameObject.GetComponent<TrackLaneRing>();
                        if (trackLaneRing != null)
                        {
                            if (position.HasValue || localPosition.HasValue)
                            {
                                _positionOffsetAccessor(ref trackLaneRing) = transform.localPosition;
                                _posZAccessor(ref trackLaneRing) = 0;
                            }

                            if (rotation.HasValue || localRotation.HasValue)
                            {
                                RingRotationOffsets[trackLaneRing] = transform.localRotation;
                                _rotZAccessor(ref trackLaneRing) = 0;
                            }
                        }

                        // Handle ParametricBoxController
                        ParametricBoxController parametricBoxController = gameObject.GetComponent<ParametricBoxController>();
                        if (parametricBoxController != null)
                        {
                            if (position.HasValue || localPosition.HasValue)
                            {
                                _parametricBoxControllerParameters.SetTransformPosition(parametricBoxController, transform.localPosition);
                            }

                            if (scale.HasValue)
                            {
                                _parametricBoxControllerParameters.SetTransformScale(parametricBoxController, transform.localScale);
                            }
                        }

                        // Handle BeatmapObjectsAvoidance
                        BeatmapObjectsAvoidance beatmapObjectsAvoidance = gameObject.GetComponent<BeatmapObjectsAvoidance>();
                        if (beatmapObjectsAvoidance != null)
                        {
                            if (position.HasValue || localPosition.HasValue)
                            {
                                AvoidancePosition[beatmapObjectsAvoidance] = transform.localPosition;
                            }

                            if (rotation.HasValue || localRotation.HasValue)
                            {
                                AvoidanceRotation[beatmapObjectsAvoidance] = transform.localRotation;
                            }
                        }

                        GameObjectTrackController? trackController = GameObjectTrackController.HandleTrackData(
                            _trackControllerFactory,
                            gameObject,
                            gameObjectData,
                            _noteLinesDistance,
                            trackLaneRing,
                            parametricBoxController,
                            beatmapObjectsAvoidance,
                            _tracks,
                            v2);
                        if (trackController != null)
                        {
                            _gameObjectTrackControllers.Add(trackController);
                        }
                    }

                    if (ChromaConfig.Instance.PrintEnvironmentEnhancementDebug)
                    {
                        Log.Logger.Log("=====================================");
                    }
                }
            }

            try
            {
                if (v2)
                {
                    LegacyEnvironmentRemoval.Init(_beatmapData);
                }
            }
            catch (Exception e)
            {
                Log.Logger.Log("Could not run Legacy Enviroment Removal");
                Log.Logger.Log(e);
            }

            HandleGeometry(_beatmapData.customData, v2);
        }

        private void HandleGeometry(CustomData customData, bool v2)
        {
            IEnumerable<CustomData>? geometriesData = customData
                .Get<List<object>>(v2 ? V2_GEOMETRY : GEOMETRY)?
                .Cast<CustomData>();

            if (geometriesData == null)
            {
                return;
            }

            // TODO: Find a better way to do this
            Transform environment = GameObject.Find("Environment").transform;

            // Specular and Standard are built in Unity
            // TODO: Make a material programatically instead of relying on this
            Material standardMaterial = new(Shader.Find("Standard"));

            Dictionary<Color, Material> materials = new();

            // Cache materials to improve bulk rendering performance
            Material GetMaterial(Color color)
            {
                if (materials.TryGetValue(color, out Material material))
                {
                    return material;
                }

                materials[color] = material = Material.Instantiate(standardMaterial);
                material.color = color;

                return material;
            }

            foreach (CustomData geometryData in geometriesData)
            {
                SpawnData spawnData = new(geometryData, v2, _noteLinesDistance);
                Color color = CustomDataManager.GetColorFromData(geometryData, v2) ?? Color.cyan;
                GeometryType? geometryType = customData.GetStringToEnum<GeometryType?>(v2 ? V2_GEOMETRY_TYPE : GEOMETRY_TYPE);

                PrimitiveType primitiveType;
                switch (geometryType)
                {
                    case GeometryType.SPHERE:
                        primitiveType = PrimitiveType.Sphere;
                        break;
                    case GeometryType.CAPSULE:
                        primitiveType = PrimitiveType.Capsule;
                        break;
                    case GeometryType.CYLINDER:
                        primitiveType = PrimitiveType.Cylinder;
                        break;
                    case GeometryType.CUBE:
                        primitiveType = PrimitiveType.Cube;
                        break;
                    case GeometryType.PLANE:
                        primitiveType = PrimitiveType.Plane;
                        break;
                    case GeometryType.QUAD:
                        primitiveType = PrimitiveType.Quad;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(geometryType), $"Geometry type {geometryType} does not match a primitive!");
                }

                GameObject gameObject = GameObject.CreatePrimitive(primitiveType);
                Transform transform = gameObject.transform;
                transform.SetParent(transform, true);
                transform.localScale = spawnData.Scale ?? Vector3.one;
                spawnData.TransformObject(transform);

                MeshRenderer meshRenderer = gameObject.GetComponent<MeshRenderer>();

                // Shared material is usually better performance as far as I know
                meshRenderer.sharedMaterial = GetMaterial(color);

                // TODO: Texture?

                GameObjectTrackController? trackController = GameObjectTrackController.HandleTrackData(
                    _trackControllerFactory,
                    gameObject,
                    geometryData,
                    _noteLinesDistance,
                    null,
                    null,
                    null,
                    _tracks,
                    v2);
                if (trackController != null)
                {
                    _gameObjectTrackControllers.Add(trackController);
                }
            }
        }

        private static void GetChildRecursive(Transform gameObject, ref List<Transform> children)
        {
            foreach (Transform child in gameObject)
            {
                children.Add(child);
                GetChildRecursive(child, ref children);
            }
        }

        // whatever the fuck rider is recommending causes shit to crash so we disable it
#pragma warning disable CA2101
        [DllImport(LOOKUPDLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern void LookupID_internal([In, Out] string[] array, int size, out IntPtr returnArray, ref int returnSize, [MarshalAs(UnmanagedType.LPStr)] string id, LookupMethod method);
#pragma warning restore CA2101

        // this is where i pretend to know what any of this is doing.
        private List<GameObjectInfo> LookupID(string[] gameObjectIds, string id, LookupMethod lookupMethod)
        {
            try
            {
                int length = gameObjectIds.Length;
                LookupID_internal(gameObjectIds, length, out IntPtr buffer, ref length, id, lookupMethod);

                int[] arrayRes = new int[length];
                Marshal.Copy(buffer, arrayRes, 0, length);
                Marshal.FreeCoTaskMem(buffer);

                List<GameObjectInfo> returnList = new(length);
                returnList.AddRange(arrayRes.Select(index => _gameObjectInfos[index]));
                return returnList;
            }
            catch (Exception e)
            {
                Log.Logger.Log("Error running LookupID, falling back to managed code.", Logger.Level.Error);
                Log.Logger.Log("Expect long load times...", Logger.Level.Error);
                Log.Logger.Log(e.ToString(), Logger.Level.Error);

                return LookupID_Legacy(id, lookupMethod);
            }
        }

        // fuck mono regex fuck mono regex fuck mono regex fuck mono regex fuck mono regex
        // fuck mono regex fuck mono regex fuck mono regex fuck mono regex fuck mono regex
        // fuck mono regex fuck mono regex fuck mono regex fuck mono regex fuck mono regex
        // fuck mono regex fuck mono regex fuck mono regex fuck mono regex fuck mono regex
        // fuck mono regex fuck mono regex fuck mono regex fuck mono regex fuck mono regex
        private List<GameObjectInfo> LookupID_Legacy(string id, LookupMethod lookupMethod)
        {
            Func<GameObjectInfo, bool> predicate;
            switch (lookupMethod)
            {
                case LookupMethod.Regex:
                    Regex regex = new(id, RegexOptions.CultureInvariant | RegexOptions.ECMAScript | RegexOptions.Compiled);
                    predicate = n => regex.IsMatch(n.FullID);
                    break;

                case LookupMethod.Exact:
                    predicate = n => n.FullID == id;
                    break;

                case LookupMethod.Contains:
                    predicate = n => n.FullID.Contains(id);
                    break;

                case LookupMethod.StartsWith:
                    predicate = n => n.FullID.StartsWith(id);
                    break;

                case LookupMethod.EndsWith:
                    predicate = n => n.FullID.EndsWith(id);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(lookupMethod), "Invalid lookup method.");
            }

            return _gameObjectInfos.Where(predicate).ToList();
        }

        private void GetAllGameObjects()
        {
            // I'll probably revist this formula for getting objects by only grabbing the root objects and adding all the children
            List<GameObject> gameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Where(n =>
            {
                if (n == null)
                {
                    return false;
                }

                string sceneName = n.scene.name;
                if (sceneName == null)
                {
                    return false;
                }

                return (sceneName.Contains("Environment") && !sceneName.Contains("Menu")) || n.GetComponent<TrackLaneRing>() != null;
            }).ToList();

            // Adds the children of whitelist GameObjects
            // Mainly for grabbing cone objects in KaleidoscopeEnvironment
            gameObjects.ToList().ForEach(n =>
            {
                List<Transform> allChildren = new();
                GetChildRecursive(n.transform, ref allChildren);

                foreach (Transform transform in allChildren)
                {
                    if (!gameObjects.Contains(transform.gameObject))
                    {
                        gameObjects.Add(transform.gameObject);
                    }
                }
            });

            List<string> objectsToPrint = new();

            foreach (GameObject gameObject in gameObjects)
            {
                GameObjectInfo gameObjectInfo = new(gameObject);
                _gameObjectInfos.Add(new GameObjectInfo(gameObject));
                objectsToPrint.Add(gameObjectInfo.FullID);

                // seriously what the fuck beat games
                // GradientBackground permanently yeeted because it looks awful and can ruin multi-colored chroma maps
                if (gameObject.name == "GradientBackground")
                {
                    gameObject.SetActive(false);
                }
            }

            if (!ChromaConfig.Instance.PrintEnvironmentEnhancementDebug)
            {
                return;
            }

            objectsToPrint.Sort();
            objectsToPrint.ForEach(n => Log.Logger.Log(n));
        }
    }
}
