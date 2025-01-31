using System;
using System.Collections.Generic;
using Anatawa12.AvatarOptimizer.Processors.TraceAndOptimizes;
using JetBrains.Annotations;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

#if AAO_VRCSDK3_AVATARS
using VRC.SDKBase;
using VRC.SDK3.Avatars.Components;
#endif

namespace Anatawa12.AvatarOptimizer.Processors.AnimatorOptimizer
{
    class AnimatorOptimizerState
    {
        private List<AOAnimatorController> _contollers = new List<AOAnimatorController>();
        public IEnumerable<AOAnimatorController> Controllers => _contollers;

        public void Add(AOAnimatorController wrapper) => _contollers.Add(wrapper);

        private Dictionary<AnimationClip, bool> _isTimeDependentClipCache = new Dictionary<AnimationClip, bool>();
        public bool IsTimeDependentClip(AnimationClip clip)
        {
            if (_isTimeDependentClipCache.TryGetValue(clip, out var result))
                return result;
            return _isTimeDependentClipCache[clip] = IsTimeDependentClipImpl(clip);
        }

        private static bool IsTimeDependentClipImpl(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);

                // one frame is time independent
                if (curve.length <= 1) continue;

                foreach (var (preKey, postKey) in curve.keys.ZipWithNext())
                {
                    var preWeighted = preKey.weightedMode == WeightedMode.Out || preKey.weightedMode == WeightedMode.Both;
                    var postWeighted = postKey.weightedMode == WeightedMode.In || postKey.weightedMode == WeightedMode.Both;

                    if (preKey.value.CompareTo(postKey.value) != 0) return true;
                    // it's constant
                    if (float.IsInfinity(preKey.outWeight) || float.IsInfinity(postKey.inTangent)) continue;
                    if (preKey.outTangent == 0 && postKey.inTangent == 0) continue;
                    if (preWeighted && postWeighted && preKey.outWeight == 0 && postKey.inWeight == 0) continue;
                    return true;
                }
            }

            return false;
        }
    }

    abstract class AnimOptPassBase<T> : TraceAndOptimizePass<T> where T : TraceAndOptimizePass<T>, new()
    {
        public override string DisplayName => "T&O: AnimOpt: " + typeof(T).Name;

        protected sealed override void Execute(BuildContext context, TraceAndOptimizeState state)
        {
            if (!state.OptimizeAnimator) return;
            foreach (var controller in context.GetState<AnimatorOptimizerState>().Controllers)
            {
                Profiler.BeginSample("Apply to Animator");
                Execute(context, controller, state);
                Profiler.EndSample();
            }
        }

        protected abstract void Execute(BuildContext context, AOAnimatorController controller,
            TraceAndOptimizeState settings);
    }

    // This pass prepares animator optimizer
    // This pass does the following things:
    // - Collects all AnimatorController objects and save to state
    // - Clones AnimatorController and StateMachines to avoid modifying original AnimatorController if needed
    // - If the RuntimeAnimatorController is AnimatorOverrideController, convert it to AnimatorController
    class InitializeAnimatorOptimizer : TraceAndOptimizePass<InitializeAnimatorOptimizer>
    {
        public override string DisplayName => "AnimOpt: Initialize";

        protected override void Execute(BuildContext context, TraceAndOptimizeState state)
        {
            if (!state.OptimizeAnimator) return;

            var animatorState = context.GetState<AnimatorOptimizerState>();

#if AAO_VRCSDK3_AVATARS
            // According to VRCSDK 3.5.0, default animation controllers doesn't have AnimatorLayerWeightControl so
            // we don't have to care about them.
            var changerBehaviours = new AnimatorLayerMap<HashSet<VRC_AnimatorLayerControl>>();
            {
                changerBehaviours[VRCAvatarDescriptor.AnimLayerType.Action] = new HashSet<VRC_AnimatorLayerControl>();
                changerBehaviours[VRCAvatarDescriptor.AnimLayerType.FX] = new HashSet<VRC_AnimatorLayerControl>();
                changerBehaviours[VRCAvatarDescriptor.AnimLayerType.Gesture] = new HashSet<VRC_AnimatorLayerControl>();
                changerBehaviours[VRCAvatarDescriptor.AnimLayerType.Additive] = new HashSet<VRC_AnimatorLayerControl>();
            }
#endif
            var clonedToController = new Dictionary<AnimatorController, AOAnimatorController>();

            foreach (var component in context.AvatarRootObject.GetComponents<Component>())
            {
                using (var serializedObject = new SerializedObject(component))
                {
                    foreach (var property in serializedObject.ObjectReferenceProperties())
                    {
                        if (property.objectReferenceValue is RuntimeAnimatorController runtimeController)
                        {
                            var cloned = AnimatorControllerCloner.Clone(context, runtimeController);
                            var wrapper = new AOAnimatorController(cloned);
                            animatorState.Add(wrapper);
                            property.objectReferenceValue = cloned;
                            clonedToController.Add(cloned, wrapper);

#if AAO_VRCSDK3_AVATARS
                            foreach (var behaviour in ACUtils.StateMachineBehaviours(cloned))
                            {
                                switch (behaviour)
                                {
                                    case VRC_AnimatorLayerControl control:
                                        if (control.playable.ToAnimLayerType() is VRCAvatarDescriptor.AnimLayerType l)
                                            changerBehaviours[l].Add(control);
                                        break;
                                }
                            }
#endif
                        }
                    }

                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            
#if AAO_VRCSDK3_AVATARS
            {
                var descriptor = context.AvatarDescriptor;
                if (descriptor && descriptor.customizeAnimationLayers)
                {
                    foreach (var playableLayer in descriptor.baseAnimationLayers)
                    {
                        if (playableLayer.isDefault || !playableLayer.animatorController ||
                            changerBehaviours[playableLayer.type] == null) continue;

                        var wrapper = clonedToController[(AnimatorController)playableLayer.animatorController];

                        foreach (var control in changerBehaviours[playableLayer.type])
                        {
                            if (control.layer < 0 || wrapper.layers.Length <= control.layer) continue;

                            var ourChange =
                                AnimatorWeightChanges.ForDurationAndWeight(control.blendDuration, control.goalWeight);

                            var layer = wrapper.layers[control.layer];

                            layer.WeightChange = layer.WeightChange.Merge(ourChange);
                            layer.LayerIndexUpdated += index => control.layer = index;
                        }

                        // process MMD world compatibility
                        if (playableLayer.type == VRCAvatarDescriptor.AnimLayerType.FX && state.MmdWorldCompatibility)
                        {
                            for (var i = 1; i <= 2; i++)
                            {
                                if (wrapper.layers.Length > i)
                                {
                                    wrapper.layers[i].MarkUnRemovable();
                                    wrapper.layers[i].WeightChange = wrapper.layers[i].WeightChange
                                        .Merge(AnimatorWeightChange.EitherZeroOrOne);
                                }
                            }
                        }
                    }
                }
            }
#endif
        }
    }

    class AnimatorControllerCloner : DeepCloneHelper
    {
        [NotNull] private readonly BuildContext _context;
        [CanBeNull] private readonly IReadOnlyDictionary<AnimationClip,AnimationClip> _mapping;

        private AnimatorControllerCloner([NotNull] BuildContext context,
            [CanBeNull] IReadOnlyDictionary<AnimationClip, AnimationClip> mapping)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _mapping = mapping;
        }

        public static AnimatorController Clone([NotNull] BuildContext context,
            [NotNull] RuntimeAnimatorController runtimeController)
        {
            var (controller, mapping) = ACUtils.GetControllerAndOverrides(runtimeController);

            return new AnimatorControllerCloner(context, mapping).MapObject(controller);
        }

        protected override Object CustomClone(Object o)
        {
            if (o is AnimationClip clip)
            {
                if (_mapping != null && _mapping.TryGetValue(clip, out var mapped))
                    return mapped;
                return clip;
            }

            return null;
        }

        protected override ComponentSupport GetComponentSupport(Object o)
        {
            switch (o)
            {
                case AnimatorController _:
                case AnimatorStateMachine _:
                case AnimatorState _:
                case AnimatorTransitionBase _:
                case StateMachineBehaviour _:
                case Motion _ :
                    return ComponentSupport.Clone;

                // should not reach this case
                case RuntimeAnimatorController _:
                    return ComponentSupport.Unsupported;

                default:
                    return ComponentSupport.NoClone;
            }
        }
    }
}
