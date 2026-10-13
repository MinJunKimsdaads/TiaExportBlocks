using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;

namespace TiaInspector
{
    internal sealed class PortalProcessInfo
    {
        public int Id;
        public string ProjectPath;
        public override string ToString()
        {
            return string.IsNullOrEmpty(ProjectPath)
                ? "PID " + Id + "  (프로젝트 없음)"
                : "PID " + Id + "  " + Path.GetFileName(ProjectPath);
        }
    }

    /// <summary>
    /// TIA Portal 연결. 이미 떠 있는 포탈에 붙거나(Attach) 프로젝트 파일을
    /// 직접 연다(Open). Attach 로 붙은 포탈은 우리 것이 아니므로 Dispose 에서
    /// 닫지 않는다 — 사용자 세션을 죽이면 안 된다.
    /// </summary>
    internal sealed class TiaSession : IDisposable
    {
        TiaPortal _portal;
        bool _ownsPortal;
        Project _openedProject;

        public TiaPortal Portal { get { return _portal; } }
        public bool IsConnected { get { return _portal != null; } }

        public static List<PortalProcessInfo> ListProcesses()
        {
            var result = new List<PortalProcessInfo>();
            foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
            {
                result.Add(new PortalProcessInfo
                {
                    Id = p.Id,
                    ProjectPath = p.ProjectPath != null ? p.ProjectPath.ToString() : null
                });
            }
            return result;
        }

        public void Attach(int processId)
        {
            foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
            {
                if (p.Id != processId) continue;
                _portal = p.Attach();
                _ownsPortal = false;
                return;
            }
            throw new InvalidOperationException("PID " + processId + " 인 TIA Portal 프로세스를 찾지 못했습니다.");
        }

        public void OpenProject(string projectFile, bool withUi)
        {
            var fi = new FileInfo(projectFile);
            if (!fi.Exists)
                throw new FileNotFoundException("프로젝트 파일이 없습니다: " + projectFile);

            _portal = new TiaPortal(withUi
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface);
            _ownsPortal = true;
            _openedProject = _portal.Projects.Open(fi);
        }

        public IEnumerable<Project> Projects
        {
            get
            {
                if (_portal == null) yield break;
                foreach (Project p in _portal.Projects) yield return p;
            }
        }

        public void Dispose()
        {
            try
            {
                // 우리가 연 프로젝트/포탈만 정리한다. 저장은 하지 않는다 —
                // TiaInspector 는 읽기 전용 도구다.
                if (_ownsPortal)
                {
                    if (_openedProject != null) _openedProject.Close();
                    if (_portal != null) _portal.Dispose();
                }
            }
            catch { /* 종료 경로에서의 실패는 무시 */ }
            finally
            {
                _openedProject = null;
                _portal = null;
            }
        }
    }
}
