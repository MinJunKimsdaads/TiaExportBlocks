using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaInspector
{
    internal sealed class ExtractOptions
    {
        public string OutputDir;
        public bool WriteXml = true;
        public bool WriteJson = true;
        public bool WriteSnapshot = true;

        /// <summary>
        /// IsConsistent == false 인 객체는 export 를 시도조차 하지 않는다.
        /// 어차피 Openness 가 거부하므로 기본값은 건너뛰기 — 끄면 실제 예외
        /// 메시지를 리포트에 남긴다(어떤 사유로 막히는지 보고 싶을 때).
        /// </summary>
        public bool SkipInconsistent = true;
    }

    internal sealed class ExtractResult
    {
        public int Exported;
        public int Failed;
        public int Skipped;
        /// <summary>Openness 가 export 자체를 지원하지 않는 언어 (ProDiag 등).</summary>
        public int Unsupported;
        public string OutputDir;
        public List<object> Items = new List<object>();
    }

    /// <summary>
    /// 선택한 CPU 한 대를 통째로 뽑아낸다 — Program blocks · Software units ·
    /// PLC data types · PLC tags.
    ///
    /// TiaInspector 는 읽기 전용이다. 컴파일을 돌리지 않으므로 프로젝트 상태를
    /// 바꾸지 않는다. inconsistent 라 나오지 않는 것은 나오지 않는 대로
    /// 리포트에 남긴다 — 조용히 빼먹지 않는 게 핵심이다.
    /// </summary>
    internal static class Extractor
    {
        public static ExtractResult Run(
            PlcEntry entry, ExtractOptions options, Action<string> log, Func<bool> cancelled)
        {
            if (log == null) log = delegate { };
            if (cancelled == null) cancelled = () => false;

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string root = Path.Combine(options.OutputDir,
                MakeValidFileName(entry.ItemName) + "_" + stamp);
            Directory.CreateDirectory(root);

            var result = new ExtractResult { OutputDir = root };

            log("출력 폴더: " + root);
            log("TIA " + VersionInfo.Label + " · CPU " + entry.ItemName
                + " (" + entry.DeviceName + ")");
            log("");

            // 1) 컴파일-프리 스냅샷 — Export 를 한 번도 부르기 전에 먼저 뜬다.
            //    여기서 나온 inconsistent 목록이 아래 export 의 예고편이다.
            if (options.WriteSnapshot)
            {
                log("[스냅샷] 객체 모델 읽는 중 (컴파일 없음)...");
                var snapshot = Snapshot.Build(entry);
                Json.WriteFile(Path.Combine(root, "snapshot.json"), snapshot);

                var summary = (Dictionary<string, object>)snapshot["summary"];
                log(string.Format(
                    "[스냅샷] 블록 {0} · 타입 {1} · 태그 {2} · inconsistent {3}",
                    summary["blockCount"], summary["typeCount"],
                    summary["tagCount"], summary["inconsistentCount"]));
                log("");
            }

            if (options.WriteXml || options.WriteJson)
            {
                // PLC 본체
                ExtractRoot(
                    root, "", entry.Software.BlockGroup, entry.Software.TypeGroup,
                    entry.Software.TagTableGroup, options, result, log, cancelled);

                // 소프트웨어 유닛 (+ 안전 유닛)
                foreach (var unit in Snapshot.EnumerateUnits(entry.Software))
                {
                    if (cancelled()) break;
                    log("");
                    log("[유닛] " + unit.Name + " (" + unit.Kind + ")");
                    string prefix = Path.Combine("Software units", MakeValidFileName(unit.Name));
                    ExtractRoot(
                        root, prefix, unit.BlockGroup, unit.TypeGroup, unit.TagTableGroup,
                        options, result, log, cancelled);
                }
            }

            // 2) 매니페스트 — 무엇이 나왔고 무엇이 안 나왔는지
            var manifest = Json.Obj();
            manifest["schema"] = "tiainspector/manifest/v1";
            manifest["generatedAt"] = DateTime.Now;
            manifest["tiaVersion"] = VersionInfo.Label;
            manifest["project"] = entry.ProjectName;
            manifest["device"] = entry.DeviceName;
            manifest["cpu"] = entry.ItemName;
            manifest["options"] = new Dictionary<string, object>
            {
                { "xml", options.WriteXml },
                { "json", options.WriteJson },
                { "snapshot", options.WriteSnapshot },
                { "skipInconsistent", options.SkipInconsistent }
            };
            manifest["exported"] = result.Exported;
            manifest["failed"] = result.Failed;
            manifest["skipped"] = result.Skipped;
            manifest["unsupported"] = result.Unsupported;
            manifest["items"] = result.Items;
            Json.WriteFile(Path.Combine(root, "manifest.json"), manifest);

            log("");
            log(string.Format("완료 — 성공 {0} · 건너뜀 {1}(inconsistent) · 미지원 {2} · 실패 {3}",
                result.Exported, result.Skipped, result.Unsupported, result.Failed));

            return result;
        }

        // ── 루트 한 벌 (PLC 본체 또는 유닛 하나) ──────────────────

        static void ExtractRoot(
            string root, string prefix,
            PlcBlockGroup blockGroup, PlcTypeSystemGroup typeGroup, PlcTagTableGroup tagGroup,
            ExtractOptions options, ExtractResult result, Action<string> log, Func<bool> cancelled)
        {
            // TIA 가 쓰는 폴더 이름을 그대로 따른다 — 나중에 되돌려 넣을 때
            // 경로가 그대로 맞아떨어진다. 순서도 import 의존성 순서를 따랐다
            // (타입 → DB → 태그 → 코드 블록).
            ExtractTypes(root, Path.Combine(prefix, "PLC data types"),
                typeGroup.Types, typeGroup.Groups, "", options, result, log, cancelled);

            ExtractBlocks(root, Path.Combine(prefix, "Program blocks"),
                blockGroup, "", options, result, log, cancelled, IsDataBlock);

            ExtractTagTables(root, Path.Combine(prefix, "PLC tags"),
                tagGroup.TagTables, GroupsOf(tagGroup), "", options, result, log, cancelled);

            ExtractBlocks(root, Path.Combine(prefix, "Program blocks"),
                blockGroup, "", options, result, log, cancelled, b => !IsDataBlock(b));
        }

        static PlcTagTableUserGroupComposition GroupsOf(PlcTagTableGroup g)
        {
            var sys = g as PlcTagTableSystemGroup;
            if (sys != null) return sys.Groups;
            var usr = g as PlcTagTableUserGroup;
            return usr != null ? usr.Groups : null;
        }

        static bool IsDataBlock(PlcBlock b)
        {
            try
            {
                ProgrammingLanguage pl = b.ProgrammingLanguage;
                return pl == ProgrammingLanguage.DB
                    || pl == ProgrammingLanguage.F_DB
                    || pl == ProgrammingLanguage.CPU_DB
                    || pl == ProgrammingLanguage.Motion_DB;
            }
            catch { return false; }
        }

        // ── 블록 ────────────────────────────────────────────────

        static void ExtractBlocks(
            string root, string section, PlcBlockGroup group, string path,
            ExtractOptions options, ExtractResult result, Action<string> log,
            Func<bool> cancelled, Func<PlcBlock, bool> filter)
        {
            foreach (PlcBlock b in group.Blocks)
            {
                if (cancelled()) return;
                if (filter != null && !filter(b)) continue;
                ExportOne(root, section, path, b.Name, "block",
                    () => IsConsistent(b), file => b.Export(file, ExportOptions.WithDefaults),
                    options, result, log);
            }

            foreach (PlcBlockGroup sub in group.Groups)
            {
                if (cancelled()) return;
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : Path.Combine(path, sub.Name);
                ExtractBlocks(root, section, sub, subPath, options, result, log, cancelled, filter);
            }
        }

        static void ExtractTypes(
            string root, string section,
            PlcTypeComposition types, PlcTypeUserGroupComposition groups, string path,
            ExtractOptions options, ExtractResult result, Action<string> log, Func<bool> cancelled)
        {
            foreach (PlcType t in types)
            {
                if (cancelled()) return;
                ExportOne(root, section, path, t.Name, "type",
                    () => IsConsistent(t), file => t.Export(file, ExportOptions.WithDefaults),
                    options, result, log);
            }

            if (groups == null) return;
            foreach (PlcTypeUserGroup sub in groups)
            {
                if (cancelled()) return;
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : Path.Combine(path, sub.Name);
                ExtractTypes(root, section, sub.Types, sub.Groups, subPath,
                    options, result, log, cancelled);
            }
        }

        static void ExtractTagTables(
            string root, string section,
            PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string path,
            ExtractOptions options, ExtractResult result, Action<string> log, Func<bool> cancelled)
        {
            foreach (PlcTagTable table in tables)
            {
                if (cancelled()) return;
                // 태그 테이블에는 IsConsistent 자체가 없다 — 컴파일 게이트가 없는
                // 유일한 갈래다. 항상 시도한다.
                ExportOne(root, section, path, table.Name, "tagTable",
                    () => true, file => table.Export(file, ExportOptions.WithDefaults),
                    options, result, log);
            }

            if (groups == null) return;
            foreach (PlcTagTableUserGroup sub in groups)
            {
                if (cancelled()) return;
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : Path.Combine(path, sub.Name);
                ExtractTagTables(root, section, sub.TagTables, sub.Groups, subPath,
                    options, result, log, cancelled);
            }
        }

        // ── 실제 export 한 건 ────────────────────────────────────

        static void ExportOne(
            string root, string section, string path, string name, string kind,
            Func<bool?> consistency, Action<FileInfo> export,
            ExtractOptions options, ExtractResult result, Action<string> log)
        {
            string relDir = Path.Combine(section, path);
            string fileName = MakeValidFileName(name);
            string display = Path.Combine(relDir, name).Replace('\\', '/');

            var item = Json.Obj();
            item["kind"] = kind;
            item["name"] = name;
            item["path"] = relDir.Replace('\\', '/');

            bool? consistent = consistency();
            item["isConsistent"] = consistent;

            if (consistent == false && options.SkipInconsistent)
            {
                item["status"] = "skipped";
                item["reason"] = "inconsistent — 컴파일 전에는 Openness 가 export 를 거부합니다";
                result.Items.Add(item);
                result.Skipped++;
                log("  [건너뜀] " + display + "  (inconsistent)");
                return;
            }

            // XML 이 산출물이면 최종 위치로 바로, 아니면 임시로 받아 JSON 만 남긴다.
            string xmlPath = options.WriteXml
                ? Path.Combine(root, "xml", relDir, fileName + ".xml")
                : Path.Combine(Path.GetTempPath(), "TiaInspector", Guid.NewGuid().ToString("N") + ".xml");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(xmlPath));
                // Openness 는 기존 파일이 있으면 던진다
                if (File.Exists(xmlPath)) File.Delete(xmlPath);

                export(new FileInfo(xmlPath));

                if (!File.Exists(xmlPath))
                    throw new IOException("Export 는 성공했다고 하는데 파일이 생기지 않았습니다");

                if (options.WriteJson)
                {
                    var converted = SimaticJson.ConvertFile(xmlPath);
                    string jsonPath = Path.Combine(root, "json", relDir, fileName + ".json");
                    Json.WriteFile(jsonPath, converted);
                }

                if (!options.WriteXml) File.Delete(xmlPath);

                item["status"] = "ok";
                result.Items.Add(item);
                result.Exported++;
                log("  [완료] " + display);
            }
            catch (Exception ex)
            {
                // ProDiag / ProDiag_OB 는 Openness 가 아예 export 를 막아 둔 언어다
                // ("The programming language 'ProDiag_OB' is not supported during
                // import and export"). 재시도해도 소용없는 제약이라 진짜 실패와
                // 섞지 않는다 — 안 그러면 원인 하나가 건수만 부풀린다.
                bool unsupported = ex.Message.IndexOf(
                    "not supported during import and export", StringComparison.OrdinalIgnoreCase) >= 0;

                item["status"] = unsupported ? "unsupported" : "error";
                item["error"] = ex.Message;
                item["errorType"] = ex.GetType().Name;
                result.Items.Add(item);

                if (unsupported)
                {
                    result.Unsupported++;
                    log("  [미지원] " + display + "  — Openness 가 이 언어의 export 를 지원하지 않습니다");
                }
                else
                {
                    result.Failed++;
                    log("  [실패] " + display + "  — " + ex.Message);
                }

                try { if (!options.WriteXml && File.Exists(xmlPath)) File.Delete(xmlPath); }
                catch { }
            }
        }

        static bool? IsConsistent(object o)
        {
            try
            {
                var block = o as PlcBlock;
                if (block != null) return block.IsConsistent;
                var type = o as PlcType;
                if (type != null) return type.IsConsistent;
            }
            catch { }
            return null;
        }

        public static string MakeValidFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
