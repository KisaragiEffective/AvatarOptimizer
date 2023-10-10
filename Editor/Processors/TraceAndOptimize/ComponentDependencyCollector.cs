using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Anatawa12.AvatarOptimizer.ErrorReporting;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using VRC.Core;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Anatawa12.AvatarOptimizer.Processors.TraceAndOptimizes
{
    /// <summary>
    /// This class collects ALL dependencies of each component
    /// </summary>
    class ComponentDependencyCollector
    {
        static ComponentDependencyCollector()
        {
            InitByTypeParsers();
        }

        private readonly bool _preserveEndBone;
        private readonly OptimizerSession _session;

        public ComponentDependencyCollector(OptimizerSession session, bool preserveEndBone)
        {
            _preserveEndBone = preserveEndBone;
            _session = session;
        }

        private readonly Dictionary<Component, ComponentDependencies> _dependencies =
            new Dictionary<Component, ComponentDependencies>();

        public class ComponentDependencies
        {
            /// <summary>
            /// True if this component has Active Meaning on the Avatar.
            /// </summary>
            public bool EntrypointComponent = false;

            /// <summary>
            /// Dependencies of this component
            /// </summary>
            [NotNull]
            public IReadOnlyDictionary<Component, (DependencyFlags flags, DependencyType type)> Dependencies => _dependencies;

            [NotNull] private readonly Dictionary<Component, (DependencyFlags, DependencyType)> _dependencies =
                new Dictionary<Component, (DependencyFlags, DependencyType)>();

            public void AddActiveDependency(Component component, bool onlyIfTargetCanBeEnabled = false,
                DependencyType kind = DependencyType.Normal)
            {
                if (!component) return;
                _dependencies.TryGetValue(component, out var pair);
                var (flags, kindFlags) = pair;
                if (!onlyIfTargetCanBeEnabled) flags |= DependencyFlags.EvenIfTargetIsDisabled;
                _dependencies[component] = (flags, kindFlags | kind);
            }

            public void AddAlwaysDependency(Component component, bool onlyIfTargetCanBeEnabled = false,
                DependencyType kind = DependencyType.Normal)
            {
                if (!component) return;
                _dependencies.TryGetValue(component, out var pair);
                var (flags, kindFlags) = pair;
                flags |= DependencyFlags.EvenIfThisIsDisabled;
                if (!onlyIfTargetCanBeEnabled) flags |= DependencyFlags.EvenIfTargetIsDisabled;
                _dependencies[component] = (flags, kindFlags | kind);
            }
        }

        [Flags]
        public enum DependencyFlags : byte
        {
            // dependency flags
            EvenIfTargetIsDisabled = 1 << 0,
            EvenIfThisIsDisabled = 1 << 1,
        }

        [Flags]
        public enum DependencyType : byte
        {
            Normal = 1 << 0,
            Parent = 1 << 1,
            ComponentToTransform = 1 << 2,
            Bone = 1 << 3,
        }

        [CanBeNull]
        public ComponentDependencies TryGetDependencies(Component dependent) =>
            _dependencies.TryGetValue(dependent, out var dependencies) ? dependencies : null;

        [NotNull]
        public ComponentDependencies GetDependencies(Component dependent) => _dependencies[dependent];

        public void CollectAllUsages()
        {
            var components = _session.GetComponents<Component>().ToArray();
            // first iteration: create mapping
            foreach (var component in components) _dependencies.Add(component, new ComponentDependencies());

            // second iteration: process parsers
            BuildReport.ReportingObjects(components, component =>
            {
                // component requires GameObject.
                GetDependencies(component).AddAlwaysDependency(component.gameObject.transform,
                    kind: DependencyType.ComponentToTransform);

                if (_byTypeParser.TryGetValue(component.GetType(), out var parser))
                {
                    var deps = GetDependencies(component);
                    parser(this, deps, component);
                }
                else
                {
                    BuildReport.LogWarning("TraceAndOptimize:warn:unknown-type", component.GetType().Name);

                    FallbackDependenciesParser(component);
                }
            });
        }

        private void FallbackDependenciesParser(Component component)
        {
            // fallback dependencies: All References are Always Dependencies.
            var dependencies = GetDependencies(component);
            dependencies.EntrypointComponent = true;
            using (var serialized = new SerializedObject(component))
            {
                var iterator = serialized.GetIterator();
                var enterChildren = true;
                while (iterator.Next(enterChildren))
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        if (iterator.objectReferenceValue is GameObject go)
                            dependencies.AddAlwaysDependency(go.transform);
                        else if (iterator.objectReferenceValue is Component com)
                            dependencies.AddAlwaysDependency(com);
                    }

                    switch (iterator.propertyType)
                    {
                        case SerializedPropertyType.String:
                        case SerializedPropertyType.Integer:
                        case SerializedPropertyType.Boolean:
                        case SerializedPropertyType.Float:
                        case SerializedPropertyType.Color:
                        case SerializedPropertyType.ObjectReference:
                        case SerializedPropertyType.LayerMask:
                        case SerializedPropertyType.Enum:
                        case SerializedPropertyType.Vector2:
                        case SerializedPropertyType.Vector3:
                        case SerializedPropertyType.Vector4:
                        case SerializedPropertyType.Rect:
                        case SerializedPropertyType.ArraySize:
                        case SerializedPropertyType.Character:
                        case SerializedPropertyType.AnimationCurve:
                        case SerializedPropertyType.Bounds:
                        case SerializedPropertyType.Gradient:
                        case SerializedPropertyType.Quaternion:
                        case SerializedPropertyType.FixedBufferSize:
                        case SerializedPropertyType.Vector2Int:
                        case SerializedPropertyType.Vector3Int:
                        case SerializedPropertyType.RectInt:
                        case SerializedPropertyType.BoundsInt:
                            enterChildren = false;
                            break;
                        case SerializedPropertyType.Generic:
                        case SerializedPropertyType.ExposedReference:
                        case SerializedPropertyType.ManagedReference:
                        default:
                            enterChildren = true;
                            break;
                    }
                }
            }
        }

        #region ByComponentMappingGeneration

        delegate void ComponentParser<in TComponent>(ComponentDependencyCollector collector, ComponentDependencies deps,
            TComponent component);

        private static readonly Dictionary<Type, ComponentParser<Component>> _byTypeParser =
            new Dictionary<Type, ComponentParser<Component>>();

        private static void AddParser<T>(ComponentParser<T> parser) where T : Component
        {
            _byTypeParser.Add(typeof(T), (collector, deps, component) => parser(collector, deps, (T)component));
        }

        private static void AddParserWithExtends<TParent, TChild>(ComponentParser<TChild> parser) 
            where TParent : Component
            where TChild : TParent
        {
            var parentParser = _byTypeParser[typeof(TParent)];
            _byTypeParser.Add(typeof(TChild), (collector, deps, component) =>
            {
                parentParser(collector, deps, component);
                parser(collector, deps, (TChild)component);
            });
        }

        private static void AddNopParser<T>() where T : Component
        {
            _byTypeParser.Add(typeof(T), (collector, deps, component) => { });
        }

        private static void AddEntryPointParser<T>() where T : Component
        {
            _byTypeParser.Add(typeof(T), (collector, deps, component) => deps.EntrypointComponent = true);
        }

        private static void AddParserWithExtends<TParent, TChild>()
            where TParent : Component
            where TChild : TParent
        {
            _byTypeParser.Add(typeof(TChild), _byTypeParser[typeof(TParent)]);
        }

        #endregion

        #region ByType Parser

        /// <summary>
        /// Initializes _byTypeParser. This includes huge amount of definition for components.
        /// </summary>
        private static void InitByTypeParsers()
        {
            // unity generic
            AddParser<Transform>((collector, deps, transform) =>
            {
                deps.AddAlwaysDependency(transform.parent, kind: DependencyType.Parent);

                // For compatibility with UnusedBonesByReferenceTool
                // https://github.com/anatawa12/AvatarOptimizer/issues/429
                if (collector._preserveEndBone &&
                    transform.name.EndsWith("end", StringComparison.OrdinalIgnoreCase))
                {
                    collector.GetDependencies(transform.parent)
                        .AddAlwaysDependency(transform);
                }
            });
            // Animator does not do much for motion, just changes states of other components.
            // All State Changes are collected separately
            AddParser<Animator>((collector, deps, component) =>
            {
                // We can have some
                deps.EntrypointComponent = true;

                // we need bone between Armature..Humanoid
                for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                {
                    var boneTransform = component.GetBoneTransform(bone);
                    foreach (var transform in boneTransform.ParentEnumerable())
                    {
                        if (transform == component.transform) break;
                        deps.AddActiveDependency(transform);
                    }
                }
            });
            AddEntryPointParser<Animation>();
            AddParser<Renderer>((collector, deps, renderer) =>
            {
                // GameObject => Renderer dependency ship
                deps.EntrypointComponent = true;
                // anchor proves
                if (renderer.reflectionProbeUsage != ReflectionProbeUsage.Off ||
                    renderer.lightProbeUsage != LightProbeUsage.Off)
                    deps.AddActiveDependency(renderer.probeAnchor);
                if (renderer.lightProbeUsage == LightProbeUsage.UseProxyVolume)
                    deps.AddActiveDependency(renderer.lightProbeProxyVolumeOverride.transform);
            });
            AddParserWithExtends<Renderer, SkinnedMeshRenderer>((collector, deps, skinnedMeshRenderer) =>
            {
                var meshInfo2 = collector._session.MeshInfo2Holder.GetMeshInfoFor(skinnedMeshRenderer);
                foreach (var bone in meshInfo2.Bones)
                    deps.AddActiveDependency(bone.Transform, kind: DependencyType.Bone);
                deps.AddActiveDependency(meshInfo2.RootBone);
            });
            AddParserWithExtends<Renderer, MeshRenderer>((collector, deps, component) =>
            {
                deps.AddActiveDependency(component.GetComponent<MeshFilter>());
            });
            AddNopParser<MeshFilter>();
            AddParser<ParticleSystem>((collector, deps, particleSystem) =>
            {
                if (particleSystem.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
                    deps.AddActiveDependency(particleSystem.main.customSimulationSpace);
                if (particleSystem.shape.enabled)
                {
                    switch (particleSystem.shape.shapeType)
                    {
                        case ParticleSystemShapeType.MeshRenderer:
                            deps.AddActiveDependency(particleSystem.shape.meshRenderer);
                            break;
                        case ParticleSystemShapeType.SkinnedMeshRenderer:
                            deps.AddActiveDependency(particleSystem.shape.skinnedMeshRenderer);
                            break;
                        case ParticleSystemShapeType.SpriteRenderer:
                            deps.AddActiveDependency(particleSystem.shape.spriteRenderer);
                            break;
#pragma warning disable CS0618
                        case ParticleSystemShapeType.Sphere:
                        case ParticleSystemShapeType.SphereShell:
                        case ParticleSystemShapeType.Hemisphere:
                        case ParticleSystemShapeType.HemisphereShell:
                        case ParticleSystemShapeType.Cone:
                        case ParticleSystemShapeType.Box:
                        case ParticleSystemShapeType.Mesh:
                        case ParticleSystemShapeType.ConeShell:
                        case ParticleSystemShapeType.ConeVolume:
                        case ParticleSystemShapeType.ConeVolumeShell:
                        case ParticleSystemShapeType.Circle:
                        case ParticleSystemShapeType.CircleEdge:
                        case ParticleSystemShapeType.SingleSidedEdge:
                        case ParticleSystemShapeType.BoxShell:
                        case ParticleSystemShapeType.BoxEdge:
                        case ParticleSystemShapeType.Donut:
                        case ParticleSystemShapeType.Rectangle:
                        case ParticleSystemShapeType.Sprite:
                        default:
#pragma warning restore CS0618
                            break;
                    }
                }

                if (particleSystem.collision.enabled)
                {
                    switch (particleSystem.collision.type)
                    {
                        case ParticleSystemCollisionType.Planes:
                            for (var i = 0; i < particleSystem.collision.maxPlaneCount; i++)
                                deps.AddActiveDependency(particleSystem.collision.GetPlane(i));
                            break;
                        case ParticleSystemCollisionType.World:
                        default:
                            break;
                    }
                }

                if (particleSystem.trigger.enabled)
                {
                    for (var i = 0; i < particleSystem.trigger.maxColliderCount; i++)
                        deps.AddActiveDependency(particleSystem.trigger.GetCollider(i));
                }

                if (particleSystem.subEmitters.enabled)
                {
                    for (var i = 0; i < particleSystem.subEmitters.subEmittersCount; i++)
                        deps.AddActiveDependency(particleSystem.subEmitters.GetSubEmitterSystem(i));
                }

                if (particleSystem.lights.enabled)
                {
                    deps.AddActiveDependency(particleSystem.lights.light);
                }

                deps.AddAlwaysDependency(particleSystem.GetComponent<ParticleSystemRenderer>());
                deps.EntrypointComponent = true;
            });
            AddParserWithExtends<Renderer, ParticleSystemRenderer>((collector, deps, component) =>
            {
                deps.AddAlwaysDependency(component.GetComponent<ParticleSystem>());
            });
            AddParserWithExtends<Renderer, TrailRenderer>();
            AddParserWithExtends<Renderer, LineRenderer>();
            AddParser<Cloth>((collector, deps, component) =>
            {
                // If Cloth is disabled, SMR work as SMR without Cloth
                // If Cloth is enabled and SMR is disabled, SMR draw nothing.
                var skinnedMesh = component.GetComponent<SkinnedMeshRenderer>();
                collector.GetDependencies(skinnedMesh).AddActiveDependency(component, true);
                foreach (var collider in component.capsuleColliders)
                    deps.AddActiveDependency(collider);
                foreach (var collider in component.sphereColliders)
                {
                    deps.AddActiveDependency(collider.first);
                    deps.AddActiveDependency(collider.second);
                }
            });
            AddEntryPointParser<Light>();
            AddParser<Collider>((collector, deps, component) =>
            {
                deps.EntrypointComponent = true;
                var rigidbody = component.GetComponentInParent<Rigidbody>();
                if (rigidbody) collector.GetDependencies(rigidbody)
                    .AddActiveDependency(component, true);
            });
            AddParserWithExtends<Collider, TerrainCollider>();
            AddParserWithExtends<Collider, BoxCollider>();
            AddParserWithExtends<Collider, SphereCollider>();
            AddParserWithExtends<Collider, MeshCollider>();
            AddParserWithExtends<Collider, CapsuleCollider>();
            AddParserWithExtends<Collider, WheelCollider>();
            AddParser<Joint>((collector, deps, component) =>
            {
                collector.GetDependencies(component.GetComponent<Rigidbody>()).AddActiveDependency(component);
                deps.AddActiveDependency(component.connectedBody);
            });
            AddParserWithExtends<Joint, CharacterJoint>();
            AddParserWithExtends<Joint, ConfigurableJoint>();
            AddParserWithExtends<Joint, FixedJoint>();
            AddParserWithExtends<Joint, HingeJoint>();
            AddParserWithExtends<Joint, SpringJoint>();
            AddParser<Rigidbody>((collector, deps, component) =>
            {
                collector.GetDependencies(component.transform)
                    .AddAlwaysDependency(component, true);
            });
            // affects RenderTexture
            AddEntryPointParser<Camera>();
            AddParser<FlareLayer>((collector, deps, component) =>
            {
                collector.GetDependencies(component.GetComponent<Camera>()).AddActiveDependency(component);
            });
            // plays sound
            AddEntryPointParser<AudioSource>();
            AddParser<AimConstraint>((collector, deps, component) =>
            {
                ConstraintParser(collector, deps, component);
                deps.AddActiveDependency(component.worldUpObject);
            });
            AddParser<LookAtConstraint>((collector, deps, component) =>
            {
                ConstraintParser(collector, deps, component);
                deps.AddActiveDependency(component.worldUpObject);
            });
            AddParser<ParentConstraint>(ConstraintParser);
            AddParser<PositionConstraint>(ConstraintParser);
            AddParser<RotationConstraint>(ConstraintParser);
            AddParser<ScaleConstraint>(ConstraintParser);

            void ConstraintParser<TConstraint>(ComponentDependencyCollector collector, ComponentDependencies deps,
                TConstraint constraint)
                where TConstraint : Component, IConstraint
            {
                collector.GetDependencies(constraint.transform)
                    .AddAlwaysDependency(constraint, true);
                for (var i = 0; i < constraint.sourceCount; i++)
                    deps.AddActiveDependency(constraint.GetSource(i).sourceTransform);
            }

            // VRChat specific
            AddParser<VRC_AvatarDescriptor>((collector, deps, component) =>
            {
                // to avoid unexpected deletion
                deps.EntrypointComponent = true;
                deps.AddAlwaysDependency(component.GetComponent<PipelineManager>());
            });
            AddParserWithExtends<VRC_AvatarDescriptor, VRCAvatarDescriptor>((collector, deps, component) =>
            {
                AddCollider(component.collider_head);
                AddCollider(component.collider_torso);
                AddCollider(component.collider_footR);
                AddCollider(component.collider_footL);
                AddCollider(component.collider_handR);
                AddCollider(component.collider_handL);
                AddCollider(component.collider_fingerIndexL);
                AddCollider(component.collider_fingerMiddleL);
                AddCollider(component.collider_fingerRingL);
                AddCollider(component.collider_fingerLittleL);
                AddCollider(component.collider_fingerIndexR);
                AddCollider(component.collider_fingerMiddleR);
                AddCollider(component.collider_fingerRingR);
                AddCollider(component.collider_fingerLittleR);

                void AddCollider(VRCAvatarDescriptor.ColliderConfig collider)
                {
                    switch (collider.state)
                    {
                        case VRCAvatarDescriptor.ColliderConfig.State.Automatic:
                        case VRCAvatarDescriptor.ColliderConfig.State.Custom:
                            deps.AddAlwaysDependency(collider.transform);
                            break;
                        case VRCAvatarDescriptor.ColliderConfig.State.Disabled:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            });
            AddEntryPointParser<PipelineManager>();
#pragma warning disable CS0618
            AddEntryPointParser<PipelineSaver>();
#pragma warning restore CS0618
            AddParser<VRC.SDKBase.VRCStation>((collector, deps, component) =>
            {
                deps.AddActiveDependency(component.stationEnterPlayerLocation);
                deps.AddActiveDependency(component.stationExitPlayerLocation);
                deps.EntrypointComponent = true;
                deps.AddActiveDependency(component.GetComponentInChildren<Collider>());
            });
            AddParserWithExtends<VRC.SDKBase.VRCStation, VRC.SDK3.Avatars.Components.VRCStation>();
            AddParser<VRCPhysBoneBase>((collector, deps, physBone) =>
            {
                // first, Transform <=> PhysBone
                // Transform is used even if the bone is inactive so Transform => PB is always dependency
                // PhysBone works only if enabled so PB => Transform is active dependency
                var ignoreTransforms = new HashSet<Transform>(physBone.ignoreTransforms);
                CollectTransforms(physBone.GetTarget());

                void CollectTransforms(Transform bone)
                {
                    collector.GetDependencies(bone)
                        .AddAlwaysDependency(physBone, true);
                    deps.AddActiveDependency(bone);
                    foreach (var child in bone.DirectChildrenEnumerable())
                    {
                        if (!ignoreTransforms.Contains(child))
                            CollectTransforms(child);
                    }
                }

                // then, PB => Collider
                // in PB, PB Colliders work only if Colliders are enabled
                foreach (var physBoneCollider in physBone.colliders)
                    deps.AddActiveDependency(physBoneCollider, true);

                // If parameter is not empty, the PB can be required for Animator Parameter so it's Entrypoint Component
                // https://github.com/anatawa12/AvatarOptimizer/issues/450
                if (!string.IsNullOrEmpty(physBone.parameter))
                    deps.EntrypointComponent = true;
            });
            AddParser<VRCPhysBoneColliderBase>((collector, deps, component) =>
            {
                deps.AddActiveDependency(component.rootTransform);
            });
            AddParserWithExtends<VRCPhysBoneBase, VRCPhysBone>();
            AddParserWithExtends<VRCPhysBoneColliderBase, VRCPhysBoneCollider>();

            AddParser<ContactBase>((collector, deps, component) =>
            {
                deps.EntrypointComponent = true;
                deps.AddActiveDependency(component.rootTransform);
            });
            AddParserWithExtends<ContactBase, ContactReceiver>();
            AddParserWithExtends<ContactReceiver, VRCContactReceiver>();
            AddParserWithExtends<ContactBase, ContactSender>();
            AddParserWithExtends<ContactSender, VRCContactSender>();

            AddEntryPointParser<VRC_SpatialAudioSource>();
            AddParserWithExtends<VRC_SpatialAudioSource, VRCSpatialAudioSource>();

            // VRC_IKFollower is not available in SDK 3

            // External library: Dynamic Bone
            if (DynamicBone.Type is Type dynamicBoneType)
            {
                _byTypeParser.Add(dynamicBoneType, (collector, deps, component) =>
                {
                    DynamicBone.TryCast(component, out var dynamicBone);
                    foreach (var transform in dynamicBone.GetAffectedTransforms())
                    {
                        collector.GetDependencies(transform)
                            .AddAlwaysDependency(component, true);
                        deps.AddActiveDependency(transform);
                    }

                    foreach (var collider in dynamicBone.Colliders)
                    {
                        // DynamicBone ignores enabled/disabled of Collider Component AFAIK
                        deps.AddActiveDependency(collider, false);
                    }
                });
                
                // ReSharper disable once PossibleNullReferenceException
                _byTypeParser.Add(ExternalLibraryAccessor.DynamicBone.ColliderType,
                    (collector, deps, component) => {});
            }

            // TODOL External Library: FinalIK

            // NDMF
            AddEntryPointParser<nadena.dev.ndmf.runtime.AvatarActivator>();
            var contextHolder = typeof(nadena.dev.ndmf.BuildContext).Assembly
                .GetType("nadena.dev.ndmf.VRChat.ContextHolder");
            if (contextHolder != null)
            {
                // nadena.dev.ndmf.VRChat.ContextHolder is internal so I use reflection
                _byTypeParser.Add(contextHolder, (collector, deps, component) => deps.EntrypointComponent = true);
            }

            // Components Proceed after T&O later
            AddEntryPointParser<MergeBone>();
        }

        #endregion
    }
}

