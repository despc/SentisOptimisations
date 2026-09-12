using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class Smoke
    {
        [ModuleInitializer]
        internal static void Init()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                foreach (var dir in TestPaths.ProbeDirs)
                {
                    var p = Path.Combine(dir, name + ".dll");
                    if (File.Exists(p)) try { return Assembly.LoadFrom(p); } catch { }
                }
                return null;
            };
        }

        [Fact]
        public void PluginAssembly_Loads()
        {
            var asm = TestPaths.PluginAssembly;
            Assert.NotNull(asm.GetType("SentisOptimisations.PatchGuard"));
        }

        [Fact]
        public void TorchTypes_Load()
        {
            var t = typeof(Torch.Managers.PatchManager.PatchContext);
            Assert.NotNull(t);
        }

        [Fact]
        public void GameAssembly_Loads()
        {
            var t = typeof(Sandbox.Game.Entities.MyCubeGrid);
            Assert.NotNull(t);
        }
    }

}
