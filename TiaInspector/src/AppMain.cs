using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace TiaInspector
{
    internal static class AppMain
    {
        [STAThread]
        static void Main()
        {
            // 어떤 Openness 타입보다 먼저 리졸버를 건다.
            OpennessResolver.Install();

            if (!VersionInfo.IsInstalled)
            {
                MessageBox.Show(
                    "TIA Portal " + VersionInfo.Label + " 의 Openness 어셈블리를 찾지 못했습니다.\n\n"
                    + "찾은 경로: " + VersionInfo.PublicApiDir + "\n\n"
                    + "이 빌드는 " + VersionInfo.Label + " 전용입니다. 다른 버전이라면 해당 버전용 "
                    + "TiaInspector 를 실행하세요.",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Run();
        }

        /// <summary>
        /// JIT 는 메서드 본문에 등장하는 타입을 그 메서드에 진입할 때 해석한다.
        /// Main 안에 Openness 타입이 보이면 리졸버를 걸기 전에 로드가 시도되므로
        /// 인라이닝을 막고 한 겹 떼어 둔다.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) =>
                ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                ShowFatal(e.ExceptionObject as Exception);

            Application.Run(new MainForm());
        }

        static void ShowFatal(Exception ex)
        {
            MessageBox.Show(
                "예기치 못한 오류입니다.\n\n" + (ex == null ? "(알 수 없음)" : ex.ToString()),
                "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
