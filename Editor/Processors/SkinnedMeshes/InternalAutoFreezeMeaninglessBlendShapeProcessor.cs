using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;

namespace Anatawa12.AvatarOptimizer.Processors.SkinnedMeshes
{
    internal class InternalAutoFreezeMeaninglessBlendShapeProcessor : EditSkinnedMeshProcessor<InternalAutoFreezeMeaninglessBlendShape>
    {
        public InternalAutoFreezeMeaninglessBlendShapeProcessor(InternalAutoFreezeMeaninglessBlendShape component) : base(component)
        {
        }

        public override EditSkinnedMeshProcessorOrder ProcessOrder => EditSkinnedMeshProcessorOrder.AutoConfigureFreezeBlendShape;

        public override void Process(BuildContext context, MeshInfo2 target)
        {
            var meaningfulBlendShapes = new HashSet<string>();

            foreach (var vertex in target.Vertices)
            foreach (var kvp in vertex.BlendShapes.Where(kvp => kvp.Value != default))
                meaningfulBlendShapes.Add(kvp.Key);

            var freezeBlendShape = Target.GetComponent<FreezeBlendShape>();
            var serialized = new SerializedObject(freezeBlendShape);
            var editorUtil = PrefabSafeSet.EditorUtil<string>.Create(
                serialized.FindProperty(nameof(FreezeBlendShape.shapeKeysSet)),
                0, p => p.stringValue, (p, v) => p.stringValue = v);
            foreach (var (meaningLess, _) in target.BlendShapes.Where(x => !meaningfulBlendShapes.Contains(x.name)))
                editorUtil.GetElementOf(meaningLess).EnsureAdded();
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // nothing to do
        public override IMeshInfoComputer GetComputer(IMeshInfoComputer upstream) => upstream;
    }
}
