using System;
using System.Collections;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaInspector
{
    internal sealed class PlcEntry
    {
        public string ProjectName;
        public string DeviceName;
        public string ItemName;      // CPU DeviceItem 이름 — 사용자가 고르는 단위
        public string TypeIdentifier;
        public PlcSoftware Software;

        public override string ToString()
        {
            return DeviceName == ItemName
                ? ItemName
                : DeviceName + " / " + ItemName;
        }
    }

    /// <summary>
    /// 프로젝트 안의 CPU(PlcSoftware) 를 전부 찾는다.
    /// Devices 최상위만 보면 그룹에 든 장비를 놓친다 — UngroupedDevicesGroup 과
    /// DeviceGroups 를 재귀로 훑어야 한다(두 프로퍼티는 버전에 따라 없을 수 있어
    /// 리플렉션으로 접근한다).
    /// </summary>
    internal static class PlcLocator
    {
        public static List<PlcEntry> FindAll(TiaSession session, Action<string> log = null)
        {
            if (log == null) log = delegate { };

            var result = new List<PlcEntry>();

            int projectCount = 0, sessionCount = 0;
            try { projectCount = session.Portal.Projects.Count; } catch (Exception ex) { log("  Projects 읽기 실패: " + ex.Message); }
            try { sessionCount = session.Portal.LocalSessions.Count; } catch { }
            log("  Projects=" + projectCount + "  LocalSessions=" + sessionCount);

            if (projectCount == 0 && sessionCount == 0)
            {
                // 이 조합(attach 는 성공, 프로젝트는 0)은 거의 항상 AllowList 문제다.
                // Openness 는 등록되지 않은 애플리케이션에 대해 예외도 대화상자도
                // 없이 빈 컬렉션을 돌려준다 — 그래서 여기서 대신 말해 준다.
                log("");
                log("  !! TIA 에 프로젝트가 열려 있는데도 0개로 보인다면 Openness AllowList 미등록입니다.");
                log("     관리자 PowerShell 에서 register-openness.ps1 을 실행한 뒤 다시 연결하세요.");
                log("     (빌드할 때마다 해시가 바뀌므로 재빌드 후에는 다시 등록해야 합니다)");
                return result;
            }

            foreach (Project proj in session.Projects)
            {
                List<Device> devices = EnumerateAllDevices(proj);
                log("  [" + proj.Name + "] 장비 " + devices.Count + "대");

                int before = result.Count;
                foreach (Device device in devices)
                    CollectFromDeviceItems(device.DeviceItems, proj.Name, device, result);
                log("  [" + proj.Name + "] CPU " + (result.Count - before) + "대");
            }
            return result;
        }

        static void CollectFromDeviceItems(
            DeviceItemComposition items, string projectName, Device device, List<PlcEntry> result)
        {
            if (items == null) return;
            foreach (DeviceItem di in items)
            {
                try
                {
                    var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                    if (sc != null && sc.Software is PlcSoftware plc)
                    {
                        result.Add(new PlcEntry
                        {
                            ProjectName = projectName,
                            DeviceName = device.Name,
                            ItemName = di.Name,
                            TypeIdentifier = SafeTypeIdentifier(device),
                            Software = plc
                        });
                    }
                }
                catch { /* software 가 없는 모듈은 그냥 넘어간다 */ }

                // Unified HMI 등은 소프트웨어가 더 깊은 자식에 있다. CPU 도
                // 랙 구성에 따라 한 단계 아래에 놓이므로 재귀한다.
                CollectFromDeviceItems(di.DeviceItems, projectName, device, result);
            }
        }

        static string SafeTypeIdentifier(Device d)
        {
            try { return d.TypeIdentifier; }
            catch { return null; }
        }

        public static List<Device> EnumerateAllDevices(Project proj)
        {
            var result = new List<Device>();
            var seen = new HashSet<string>();
            if (proj == null) return result;

            try
            {
                foreach (Device d in proj.Devices)
                    if (d != null && d.Name != null && seen.Add(d.Name)) result.Add(d);
            }
            catch { }

            // 분산 IO 슬레이브가 들어가는 곳
            try
            {
                var ug = proj.GetType().GetProperty("UngroupedDevicesGroup")?.GetValue(proj, null);
                if (ug != null) CollectFromDeviceGroup(ug, result, seen);
            }
            catch { }

            // 사용자 정의 그룹 (재귀)
            try
            {
                var groups = proj.GetType().GetProperty("DeviceGroups")?.GetValue(proj, null) as IEnumerable;
                if (groups != null)
                    foreach (var g in groups) CollectFromDeviceGroup(g, result, seen);
            }
            catch { }

            return result;
        }

        static void CollectFromDeviceGroup(object group, List<Device> result, HashSet<string> seen)
        {
            if (group == null) return;
            try
            {
                var devices = group.GetType().GetProperty("Devices")?.GetValue(group, null) as IEnumerable;
                if (devices != null)
                    foreach (Device d in devices)
                        if (d != null && d.Name != null && seen.Add(d.Name)) result.Add(d);
            }
            catch { }

            try
            {
                var subs = group.GetType().GetProperty("Groups")?.GetValue(group, null) as IEnumerable;
                if (subs != null)
                    foreach (var sub in subs) CollectFromDeviceGroup(sub, result, seen);
            }
            catch { }
        }
    }
}
