using System;
using System.IO;

namespace TiaInspector
{
    /// <summary>
    /// 이 빌드가 어느 TIA 메이저를 대상으로 컴파일됐는지. 버전별 csproj 가
    /// TIA_V19 / TIA_V20 / TIA_V21 중 하나를 정의한다.
    ///
    /// Openness 어셈블리 레이아웃(실측):
    ///   V19/V20  PublicAPI\V{n}\Siemens.Engineering.dll (+ .Hmi.dll)
    ///   V21      PublicAPI\V21\net48\ 아래로 분해 —
    ///            Base(TiaPortal·Project·HW·Compiler) · Step7(SW.*) ·
    ///            WinCC · WinCCUnified · Startdrive
    /// 네임스페이스와 타입 이름은 전부 보존됐다. 그래서 src/ 는 한 벌이고
    /// 바뀌는 건 csproj 의 참조 목록과 아래 경로 규칙뿐이다.
    /// </summary>
    internal static class VersionInfo
    {
#if TIA_V19
        public const int Major = 19;
#elif TIA_V20
        public const int Major = 20;
#elif TIA_V21
        public const int Major = 21;
#else
#error TIA_V19 / TIA_V20 / TIA_V21 중 하나를 DefineConstants 로 정의해야 합니다.
#endif

        /// <summary>V21 부터 PublicAPI 아래 net48 타깃 폴더가 한 단계 더 있다.</summary>
        public const int AssemblyLayoutSplitFrom = 21;

        public static string Label { get { return "V" + Major; } }

        public static string InstallDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Siemens\Automation\Portal V" + Major);
            }
        }

        public static string PublicApiDir
        {
            get
            {
                string baseDir = Path.Combine(InstallDir, "PublicAPI", Label);
                return Major >= AssemblyLayoutSplitFrom
                    ? Path.Combine(baseDir, "net48")
                    : baseDir;
            }
        }

        /// <summary>Siemens.Engineering.Contract.dll 이 여기 있다 (전 버전 공통).</summary>
        public static string BinPublicApiDir
        {
            get { return Path.Combine(InstallDir, "Bin", "PublicAPI"); }
        }

        public static bool IsInstalled
        {
            get { return Directory.Exists(PublicApiDir); }
        }
    }
}
