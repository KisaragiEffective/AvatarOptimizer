using System;
using System.Collections.Generic;
using System.Linq;
using Anatawa12.AvatarOptimizer.Processors.SkinnedMeshes;
using JetBrains.Annotations;
using nadena.dev.ndmf;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = System.Diagnostics.Debug;

namespace Anatawa12.AvatarOptimizer.Processors
{
    internal class MeshInfo2Context : IExtensionContext
    {
        [CanBeNull] public MeshInfo2Holder Holder { get; private set; }
        public void OnActivate(BuildContext context)
        {
            Holder = new MeshInfo2Holder(context.AvatarRootObject);
        }

        public void OnDeactivate(BuildContext context)
        {
            Debug.Assert(Holder != null, nameof(Holder) + " != null");
            // avoid Array index (n) is out of bounds (size=m) error
            // by assigning null to AnimatorController before changing blendShapes count
            // and assigning back after changing blendShapes count.
            // see https://github.com/anatawa12/AvatarOptimizer/issues/804
            using (new EvacuateAnimatorController(context))
            {
                Holder.SaveToMesh();
            }
            Holder = null;
        }
    }

    internal struct EvacuateAnimatorController : IDisposable
    {
        private Dictionary<Animator, RuntimeAnimatorController> _controllers;

        public EvacuateAnimatorController(BuildContext context)
        {
            _controllers = context.GetComponents<Animator>()
                .ToDictionary(a => a, a => a.runtimeAnimatorController);

            foreach (var animator in _controllers.Keys)
                animator.runtimeAnimatorController = null;
        }

        public void Dispose()
        {
            foreach (var (animator, runtimeAnimatorController) in _controllers)
                animator.runtimeAnimatorController = runtimeAnimatorController;
        }
    }

    internal class MeshInfo2Holder
    {
        private readonly Dictionary<SkinnedMeshRenderer, MeshInfo2> _skinnedCache =
            new Dictionary<SkinnedMeshRenderer, MeshInfo2>();

        public MeshInfo2Holder(GameObject rootObject)
        {
            var avatarTagComponent = rootObject.GetComponentInChildren<AvatarTagComponent>(true);
            if (avatarTagComponent == null) return;
            foreach (var renderer in rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Profiler.BeginSample($"Read Skinned Mesh");
                GetMeshInfoFor(renderer);
                Profiler.EndSample();
            }
        }

        public MeshInfo2 GetMeshInfoFor(SkinnedMeshRenderer renderer) =>
            _skinnedCache.TryGetValue(renderer, out var cached)
                ? cached
                : _skinnedCache[renderer] = new MeshInfo2(renderer);


        public void SaveToMesh()
        {
            foreach (var keyValuePair in _skinnedCache)
            {
                var targetRenderer = keyValuePair.Key;
                if (!targetRenderer) continue;

                Profiler.BeginSample($"Save Skinned Mesh {targetRenderer.name}");
                keyValuePair.Value.WriteToSkinnedMeshRenderer(targetRenderer);
                Profiler.EndSample();
            }
        }
    }
}
