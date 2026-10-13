using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TiaInspector
{
    internal sealed class MainForm : Form
    {
        readonly OpennessWorker _worker = new OpennessWorker();
        readonly TiaSession _session = new TiaSession();
        List<PlcEntry> _plcs = new List<PlcEntry>();
        volatile bool _cancelRequested;
        volatile bool _busy;
        string _lastOutputDir;

        // ① 연결
        RadioButton _rbAttach, _rbOpen;
        ComboBox _cboProcesses;
        Button _btnRefreshProcesses, _btnBrowseProject, _btnConnect;
        TextBox _txtProjectPath;
        CheckBox _chkWithUi;

        // ② CPU
        ListBox _lstPlcs;
        Button _btnScan;

        // ③ 옵션
        CheckBox _chkXml, _chkJson, _chkSnapshot, _chkSkipInconsistent;
        TextBox _txtOutput;
        Button _btnBrowseOutput, _btnExtract, _btnCancel, _btnOpenFolder;

        // ④ 로그
        TextBox _txtLog;
        ToolStripStatusLabel _status;

        public MainForm()
        {
            Text = "TiaInspector — TIA " + VersionInfo.Label;
            Width = 820;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            MinimumSize = new Size(700, 600);

            BuildUi();
            SetBusy(false);
            RefreshProcesses();
        }

        // ── UI 구성 ────────────────────────────────────────────

        void BuildUi()
        {
            var connect = new GroupBox
            {
                Text = "① 연결",
                Left = 12, Top = 10, Width = 780, Height = 130,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _rbAttach = new RadioButton
            {
                Text = "실행 중인 TIA Portal 에 연결",
                Left = 14, Top = 24, Width = 220, Checked = true
            };
            _cboProcesses = new ComboBox
            {
                Left = 240, Top = 22, Width = 400,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnRefreshProcesses = new Button
            {
                Text = "새로고침", Left = 648, Top = 21, Width = 110,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnRefreshProcesses.Click += (s, e) => RefreshProcesses();

            _rbOpen = new RadioButton
            {
                Text = "프로젝트 파일 열기", Left = 14, Top = 56, Width = 220
            };
            _txtProjectPath = new TextBox
            {
                Left = 240, Top = 54, Width = 400,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnBrowseProject = new Button
            {
                Text = "찾아보기...", Left = 648, Top = 53, Width = 110,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBrowseProject.Click += BrowseProject;

            _chkWithUi = new CheckBox
            {
                Text = "TIA 창 표시 (끄면 백그라운드로 열립니다 — 더 빠름)",
                Left = 240, Top = 82, Width = 400, Checked = true
            };

            _btnConnect = new Button
            {
                Text = "연결", Left = 648, Top = 88, Width = 110, Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnConnect.Click += Connect;

            connect.Controls.AddRange(new Control[]
            {
                _rbAttach, _cboProcesses, _btnRefreshProcesses,
                _rbOpen, _txtProjectPath, _btnBrowseProject, _chkWithUi, _btnConnect
            });

            var cpu = new GroupBox
            {
                Text = "② CPU 선택",
                Left = 12, Top = 148, Width = 780, Height = 150,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _lstPlcs = new ListBox
            {
                Left = 14, Top = 22, Width = 624, Height = 112,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
                       | AnchorStyles.Right | AnchorStyles.Bottom,
                IntegralHeight = false
            };
            _btnScan = new Button
            {
                Text = "CPU 다시 찾기", Left = 648, Top = 22, Width = 110,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnScan.Click += (s, e) => ScanPlcs();
            cpu.Controls.AddRange(new Control[] { _lstPlcs, _btnScan });

            var options = new GroupBox
            {
                Text = "③ 추출 옵션",
                Left = 12, Top = 306, Width = 780, Height = 130,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _chkXml = new CheckBox
            {
                Text = "SimaticML XML (원본)", Left = 14, Top = 24, Width = 170, Checked = true
            };
            _chkJson = new CheckBox
            {
                Text = "JSON (구조화)", Left = 194, Top = 24, Width = 140, Checked = true
            };
            _chkSnapshot = new CheckBox
            {
                Text = "컴파일-프리 스냅샷", Left = 344, Top = 24, Width = 160, Checked = true
            };
            _chkSkipInconsistent = new CheckBox
            {
                Text = "inconsistent 건너뛰기 (끄면 실패 사유를 리포트에 남깁니다)",
                Left = 14, Top = 50, Width = 480, Checked = true
            };

            var lblOut = new Label { Text = "출력", Left = 14, Top = 82, Width = 40 };
            _txtOutput = new TextBox
            {
                Left = 56, Top = 79, Width = 470,
                Text = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "TiaInspector"),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnBrowseOutput = new Button
            {
                Text = "...", Left = 532, Top = 78, Width = 36,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnBrowseOutput.Click += BrowseOutput;

            _btnExtract = new Button
            {
                Text = "추출 시작", Left = 578, Top = 76, Width = 90, Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnExtract.Click += Extract;

            _btnCancel = new Button
            {
                Text = "중지", Left = 672, Top = 76, Width = 60, Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnCancel.Click += (s, e) => { _cancelRequested = true; Log("중지 요청됨 — 진행 중인 항목까지 마치고 멈춥니다."); };

            _btnOpenFolder = new Button
            {
                Text = "📂", Left = 736, Top = 76, Width = 34, Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnOpenFolder.Click += (s, e) =>
            {
                string dir = _lastOutputDir ?? _txtOutput.Text;
                if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
            };

            options.Controls.AddRange(new Control[]
            {
                _chkXml, _chkJson, _chkSnapshot, _chkSkipInconsistent,
                lblOut, _txtOutput, _btnBrowseOutput, _btnExtract, _btnCancel, _btnOpenFolder
            });

            var logBox = new GroupBox
            {
                Text = "④ 로그",
                Left = 12, Top = 444, Width = 780, Height = 250,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
                       | AnchorStyles.Right | AnchorStyles.Bottom
            };
            _txtLog = new TextBox
            {
                Left = 14, Top = 22, Width = 752, Height = 216,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.White,
                Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
                       | AnchorStyles.Right | AnchorStyles.Bottom
            };
            logBox.Controls.Add(_txtLog);

            var statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("연결되지 않음");
            statusStrip.Items.Add(_status);

            Controls.AddRange(new Control[] { connect, cpu, options, logBox, statusStrip });
        }

        // ── 연결 ───────────────────────────────────────────────

        void RefreshProcesses()
        {
            _cboProcesses.Items.Clear();
            _worker.Post(() =>
            {
                List<PortalProcessInfo> list;
                try { list = TiaSession.ListProcesses(); }
                catch (Exception ex)
                {
                    Log("프로세스 목록 조회 실패: " + ex.Message);
                    return;
                }

                Ui(() =>
                {
                    foreach (PortalProcessInfo p in list) _cboProcesses.Items.Add(p);
                    if (_cboProcesses.Items.Count > 0) _cboProcesses.SelectedIndex = 0;
                    Log(list.Count == 0
                        ? "실행 중인 TIA Portal 이 없습니다. 프로젝트 파일 열기를 쓰거나 TIA 를 먼저 띄우세요."
                        : "TIA Portal 프로세스 " + list.Count + "개 발견");
                });
            });
        }

        void BrowseProject(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "TIA 프로젝트 파일 선택";
                dlg.Filter = string.Format(
                    "TIA {0} 프로젝트 (*.ap{1})|*.ap{1}|모든 파일 (*.*)|*.*",
                    VersionInfo.Label, VersionInfo.Major);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _txtProjectPath.Text = dlg.FileName;
                _rbOpen.Checked = true;
            }
        }

        void Connect(object sender, EventArgs e)
        {
            if (_busy) return;

            bool attach = _rbAttach.Checked;
            var selected = _cboProcesses.SelectedItem as PortalProcessInfo;
            string projectPath = _txtProjectPath.Text.Trim();
            bool withUi = _chkWithUi.Checked;

            if (attach && selected == null)
            {
                MessageBox.Show(this, "연결할 TIA Portal 프로세스를 고르세요.",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!attach && string.IsNullOrEmpty(projectPath))
            {
                MessageBox.Show(this, "프로젝트 파일을 고르세요.",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            Status("연결 중...");

            _worker.Post(() =>
            {
                try
                {
                    if (attach)
                    {
                        Log("PID " + selected.Id + " 에 연결 중...");
                        _session.Attach(selected.Id);
                    }
                    else
                    {
                        Log("프로젝트 여는 중: " + projectPath);
                        Log("(대형 프로젝트는 몇 분 걸립니다)");
                        _session.OpenProject(projectPath, withUi);
                    }

                    int? actual = OpennessResolver.ActualLoadedMajor();
                    Log("연결됨 — Openness " + (actual.HasValue ? "V" + actual.Value : "?")
                        + " (" + (OpennessResolver.ResolvedFrom ?? "기본 탐색 경로") + ")");

                    if (actual.HasValue && actual.Value != VersionInfo.Major)
                    {
                        // 이런 일이 생기면 조용히 잘못된 버전으로 읽게 된다.
                        Log("!! 경고: 이 빌드는 " + VersionInfo.Label + " 대상인데 실제로는 V"
                            + actual.Value + " 어셈블리가 로드됐습니다.");
                    }

                    Ui(() => Status("연결됨"));
                    ScanPlcsCore();
                }
                catch (Exception ex)
                {
                    Log("연결 실패: " + ex.Message);
                    Ui(() => Status("연결 실패"));
                }
                finally
                {
                    Ui(() => SetBusy(false));
                }
            });
        }

        // ── CPU 스캔 ───────────────────────────────────────────

        void ScanPlcs()
        {
            if (_busy) return;
            if (!_session.IsConnected) { Log("먼저 연결하세요."); return; }
            SetBusy(true);
            _worker.Post(() =>
            {
                try { ScanPlcsCore(); }
                finally { Ui(() => SetBusy(false)); }
            });
        }

        /// <summary>Openness 스레드 위에서만 호출할 것.</summary>
        void ScanPlcsCore()
        {
            try
            {
                Log("CPU 검색 중...");
                List<PlcEntry> found = PlcLocator.FindAll(_session, Log);
                _plcs = found;
                Ui(() =>
                {
                    _lstPlcs.Items.Clear();
                    foreach (PlcEntry p in found)
                        _lstPlcs.Items.Add(p.ProjectName + "  ▸  " + p);
                    if (_lstPlcs.Items.Count > 0) _lstPlcs.SelectedIndex = 0;
                });
                Log("CPU " + found.Count + "대 발견"
                    + (found.Count == 0 ? " — 프로젝트가 열려 있는지 확인하세요." : ""));
            }
            catch (Exception ex)
            {
                Log("CPU 조회 실패: " + ex.Message);
            }
        }

        // ── 추출 ───────────────────────────────────────────────

        void BrowseOutput(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "추출 결과를 저장할 폴더";
                if (Directory.Exists(_txtOutput.Text)) dlg.SelectedPath = _txtOutput.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtOutput.Text = dlg.SelectedPath;
            }
        }

        void Extract(object sender, EventArgs e)
        {
            if (_busy) return;

            int index = _lstPlcs.SelectedIndex;
            if (index < 0 || index >= _plcs.Count)
            {
                MessageBox.Show(this, "추출할 CPU 를 고르세요.",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!_chkXml.Checked && !_chkJson.Checked && !_chkSnapshot.Checked)
            {
                MessageBox.Show(this, "산출물을 최소 하나는 고르세요.",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PlcEntry entry = _plcs[index];
            var options = new ExtractOptions
            {
                OutputDir = _txtOutput.Text.Trim(),
                WriteXml = _chkXml.Checked,
                WriteJson = _chkJson.Checked,
                WriteSnapshot = _chkSnapshot.Checked,
                SkipInconsistent = _chkSkipInconsistent.Checked
            };

            _cancelRequested = false;
            SetBusy(true);
            Status("추출 중...");
            _txtLog.Clear();

            var stopwatch = Stopwatch.StartNew();
            _worker.Post(() =>
            {
                try
                {
                    ExtractResult result = Extractor.Run(entry, options, Log, () => _cancelRequested);
                    _lastOutputDir = result.OutputDir;
                    stopwatch.Stop();
                    Log("소요 " + stopwatch.Elapsed.TotalSeconds.ToString("F1") + "초");
                    Ui(() => Status(string.Format(
                        "완료 — 성공 {0} · 실패 {1} · 건너뜀 {2}",
                        result.Exported, result.Failed, result.Skipped)));
                }
                catch (Exception ex)
                {
                    Log("추출 실패: " + ex);
                    Ui(() => Status("추출 실패"));
                }
                finally
                {
                    Ui(() => SetBusy(false));
                }
            });
        }

        // ── 잡다 ───────────────────────────────────────────────

        void SetBusy(bool busy)
        {
            _busy = busy;
            _btnConnect.Enabled = !busy;
            _btnScan.Enabled = !busy;
            _btnExtract.Enabled = !busy;
            _btnRefreshProcesses.Enabled = !busy;
            _btnCancel.Enabled = busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        void Status(string text)
        {
            _status.Text = text;
        }

        void Log(string line)
        {
            Ui(() =>
            {
                _txtLog.AppendText(line + Environment.NewLine);
                _txtLog.SelectionStart = _txtLog.TextLength;
                _txtLog.ScrollToCaret();
            });
        }

        /// <summary>어느 스레드에서 불러도 UI 스레드에서 실행되게 한다.</summary>
        void Ui(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { /* 폼이 닫히는 중 */ }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                DialogResult answer = MessageBox.Show(this,
                    "작업이 진행 중입니다. 정말 닫을까요?",
                    "TiaInspector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
                _cancelRequested = true;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 세션도 Openness 스레드에서 정리한다. Dispose() 가 큐를 닫고
                // 남은 작업을 흘려보낸 뒤 Join 하므로 이 순서로 안전하다.
                try { _worker.Post(() => _session.Dispose()); } catch { }
                try { _worker.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
