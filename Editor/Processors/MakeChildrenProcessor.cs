using Anatawa12.AvatarOptimizer.ErrorReporting;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace Anatawa12.AvatarOptimizer.Processors
{
    internal class MakeChildrenProcessor
    {
        private readonly bool _early;

        public MakeChildrenProcessor(bool early)
        {
            _early = early;
        }

        public void Process(BuildContext context)
        {
            BuildReport.ReportingObjects(context.GetComponents<MakeChildren>(), makeChildren =>
            {
                if (makeChildren.executeEarly != _early) return;
                foreach (var makeChildrenChild in makeChildren.children.GetAsSet().Where(x => x))
                {
                    makeChildrenChild.parent = makeChildren.transform;
                }
                Object.DestroyImmediate(makeChildren);
            });
        }
    }
}
