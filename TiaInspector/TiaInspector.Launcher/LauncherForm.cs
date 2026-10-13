using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TiaInspector.Launcher
{
    internal sealed class LauncherForm : Form
    {
        readonly List<TiaInstall> _installs;
        ListView _list;
        Button _btnLaunch, _btnRescan;
        Label _hint;

        public LauncherForm(List<TiaInstall> installs)
        {
            _installs = installs;

            Text = "TiaInspector";
            Width = 620;
            Height = 380;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            BuildUi();
            Populate(_installs);
        }

        void BuildUi()
        {
            var title = new Label
            {
                Text = "TIA Portal 버전 선택",
                Left = 16, Top = 14, Width = 400,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };

            var subtitle = new Label
            {
                Text = ".NET 은 한 프로세스에 Openness 어셈블리를 한 번만 로드할 수 있고 "
                     + "교체가 불가능합니다. 그래서 버전마다 전용 프로세스를 띄웁니다.",
                Left = 16, Top = 40, Width = 570, Height = 32,
                ForeColor = SystemColors.GrayText
            };

            _list = new ListView
            {
                Left = 16, Top = 80, Width = 570, Height = 180,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = false
            };
            _list.Columns.Add("버전", 70);
            _list.Columns.Add("Openness", 90);
            _list.Columns.Add("TIA 실행", 140);
            _list.Columns.Add("TiaInspector 빌드", 130);
            _list.Columns.Add("설치 경로", 300);
            _list.DoubleClick += (s, e) => Launch();
            _list.SelectedIndexChanged += (s, e) => UpdateHint();

            _hint = new Label
            {
                Left = 16, Top = 268, Width = 400, Height = 32,
                ForeColor = SystemColors.GrayText
            };

            _btnRescan = new Button { Text = "다시 검색", Left = 380, Top = 300, Width = 100, Height = 30 };
            _btnRescan.Click += (s, e) => Populate(TiaInstallScanner.Scan());

            _btnLaunch = new Button { Text = "실행", Left = 486, Top = 300, Width = 100, Height = 30 };
            _btnLaunch.Click += (s, e) => Launch();
            AcceptButton = _btnLaunch;

            Controls.AddRange(new Control[] { title, subtitle, _list, _hint, _btnRescan, _btnLaunch });
        }

        void Populate(List<TiaInstall> installs)
        {
            _installs.Clear();
            _installs.AddRange(installs);

            _list.Items.Clear();
            TiaInstall preferred = Preferred(installs);

            foreach (TiaInstall t in installs)
            {
                var row = new ListViewItem(t.Label);
                row.SubItems.Add(t.HasOpenness ? "설치됨" : "없음");
                row.SubItems.Add(t.IsRunning
                    ? "실행 중 (PID " + string.Join(", ", t.RunningPids.ConvertAll(p => p.ToString()).ToArray()) + ")"
                    : "-");
                row.SubItems.Add(t.WorkerExe != null ? "있음" : "없음");
                row.SubItems.Add(t.InstallDir ?? "-");
                row.Tag = t;

                if (!t.Launchable) row.ForeColor = SystemColors.GrayText;
                if (t == preferred)
                {
                    row.Font = new Font(_list.Font, FontStyle.Bold);
                    row.Selected = true;
                }
                _list.Items.Add(row);
            }

            if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
                _list.Items[0].Selected = true;

            _list.Focus();
            UpdateHint();
        }

        /// <summary>떠 있는 TIA 가 있으면 그 버전이 맞을 확률이 가장 높다.</summary>
        static TiaInstall Preferred(List<TiaInstall> installs)
        {
            foreach (TiaInstall t in installs)
                if (t.IsRunning && t.Launchable) return t;
            foreach (TiaInstall t in installs)
                if (t.Launchable) return t;
            return null;
        }

        TiaInstall Selected
        {
            get
            {
                return _list.SelectedItems.Count > 0
                    ? _list.SelectedItems[0].Tag as TiaInstall
                    : null;
            }
        }

        void UpdateHint()
        {
            TiaInstall t = Selected;
            if (t == null) { _hint.Text = ""; _btnLaunch.Enabled = false; return; }

            _btnLaunch.Enabled = t.Launchable;
            if (!t.HasOpenness)
                _hint.Text = t.Label + ": Openness 가 설치돼 있지 않습니다.";
            else if (t.WorkerExe == null)
                _hint.Text = t.Label + ": TiaInspector.V" + t.Major + ".exe 가 없습니다. "
                           + "해당 버전으로 빌드하세요.";
            else
                _hint.Text = "";
        }

        void Launch()
        {
            TiaInstall t = Selected;
            if (t == null || !t.Launchable) return;

            try
            {
                Process.Start(new ProcessStartInfo(t.WorkerExe) { UseShellExecute = false });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "실행 실패: " + ex.Message,
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
