using System;
using System.IO;
using System.Reflection;

namespace TiaInspector
{
    /// <summary>
    /// Openness 어셈블리를 설치 경로에서 찾아 준다.
    ///
    /// 반드시 Openness 타입을 건드리는 어떤 메서드보다 **먼저** Install() 이
    /// 실행돼야 한다. JIT 는 메서드 본문에 등장하는 타입을 메서드 진입 시점에
    /// 해석하므로, Main 안에서 Openness 타입을 직접 언급하면 핸들러를 등록하기
    /// 전에 이미 로드가 시도된다 — AppMain 이 그래서 NoInlining 으로 한 겹
    /// 감싸 둔다.
    /// </summary>
    internal static class OpennessResolver
    {
        static bool _installed;
        static string _resolvedFrom;

        /// <summary>실제로 어셈블리를 집어온 폴더 (진단용).</summary>
        public static string ResolvedFrom { get { return _resolvedFrom; } }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string fileName = new AssemblyName(args.Name).Name + ".dll";

                // 1) PublicAPI (V21 은 net48 하위) — Siemens.Engineering* 본체
                string p1 = Path.Combine(VersionInfo.PublicApiDir, fileName);
                if (File.Exists(p1))
                {
                    _resolvedFrom = VersionInfo.PublicApiDir;
                    return Assembly.LoadFrom(p1);
                }

                // 2) Bin\PublicAPI — Siemens.Engineering.Contract.dll 등
                string p2 = Path.Combine(VersionInfo.BinPublicApiDir, fileName);
                if (File.Exists(p2))
                {
                    _resolvedFrom = VersionInfo.BinPublicApiDir;
                    return Assembly.LoadFrom(p2);
                }

                return null;
            };
        }

        /// <summary>
        /// AppDomain 에 실제로 올라온 Openness 코어의 메이저 버전.
        /// V19/V20 은 Siemens.Engineering, V21 은 Siemens.Engineering.Base 가
        /// TiaPortal 을 담은 코어다. 요청 버전과 실제가 어긋나면 조용히 잘못된
        /// 버전으로 작업하게 되므로 기동 직후 대조한다.
        /// </summary>
        public static int? ActualLoadedMajor()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; }
                catch { continue; }

                if (name != "Siemens.Engineering" && name != "Siemens.Engineering.Base")
                    continue;

                try { return asm.GetName().Version.Major; }
                catch { return null;
                }
            }
            return null;
        }
    }
}
