using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

namespace TiaInspector
{
    /// <summary>
    /// 컴파일-프리 스냅샷. Export() 를 한 번도 부르지 않고 객체 모델만 읽는다.
    ///
    /// Export 가 요구하는 건 컴파일이 아니라 IsConsistent 다. 그리고 그 값
    /// 자체는 컴파일 없이 읽힌다 — 그래서 "무엇을 export 할 수 있는지"를
    /// 프로젝트를 건드리지 않고 먼저 알 수 있다. 태그와 상수는 아예 XML 이
    /// 필요 없고(객체 모델에 전량 노출), 블록/UDT 는 메타데이터까지 나온다.
    /// 인터페이스 멤버·DB 시작값·네트워크 로직은 여기 없다 — XML 로 가야 한다.
    /// </summary>
    internal static class Snapshot
    {
        public static Dictionary<string, object> Build(PlcEntry entry)
        {
            var roots = new List<object>();
            var inconsistent = new List<object>();
            int blockCount = 0, typeCount = 0, tagCount = 0;

            roots.Add(BuildRoot(
                "plc", entry.ItemName, null, null,
                entry.Software.BlockGroup, entry.Software.TypeGroup, entry.Software.TagTableGroup,
                inconsistent, ref blockCount, ref typeCount, ref tagCount));

            foreach (var unit in EnumerateUnits(entry.Software))
            {
                roots.Add(BuildRoot(
                    unit.Kind, unit.Name, unit.NamespacePreset, unit.Author,
                    unit.BlockGroup, unit.TypeGroup, unit.TagTableGroup,
                    inconsistent, ref blockCount, ref typeCount, ref tagCount));
            }

            var result = Json.Obj();
            result["schema"] = "tiainspector/snapshot/v1";
            result["generatedAt"] = DateTime.Now;
            result["tiaVersion"] = VersionInfo.Label;
            result["project"] = entry.ProjectName;
            result["device"] = entry.DeviceName;
            result["cpu"] = entry.ItemName;
            result["typeIdentifier"] = entry.TypeIdentifier;
            result["roots"] = roots;

            var summary = Json.Obj();
            summary["blockCount"] = blockCount;
            summary["typeCount"] = typeCount;
            summary["tagCount"] = tagCount;
            summary["inconsistentCount"] = inconsistent.Count;
            // export 를 막는 건 이 목록뿐이다. 비어 있으면 컴파일 0회로 전부 나온다.
            summary["inconsistent"] = inconsistent;
            result["summary"] = summary;

            return result;
        }

        // ── 루트(PLC 본체 / 소프트웨어 유닛) 한 벌 ──────────────────

        static Dictionary<string, object> BuildRoot(
            string kind, string name, string namespacePreset, string author,
            PlcBlockGroup blockGroup, PlcTypeSystemGroup typeGroup, PlcTagTableGroup tagGroup,
            List<object> inconsistent, ref int blockCount, ref int typeCount, ref int tagCount)
        {
            var blocks = new List<object>();
            CollectBlocks(blockGroup, "", name, blocks, inconsistent);
            blockCount += blocks.Count;

            var types = new List<object>();
            CollectTypes(typeGroup.Types, typeGroup.Groups, "", name, types, inconsistent);
            typeCount += types.Count;

            var tables = new List<object>();
            int tagsHere = 0;
            CollectTagTables(tagGroup.TagTables, GroupsOf(tagGroup), "", tables, ref tagsHere);
            tagCount += tagsHere;

            var root = Json.Obj();
            root["root"] = kind;
            root["name"] = name;
            if (namespacePreset != null) root["namespacePreset"] = namespacePreset;
            if (author != null) root["author"] = author;
            root["blocks"] = blocks;
            root["types"] = types;
            root["tagTables"] = tables;
            return root;
        }

        static PlcTagTableUserGroupComposition GroupsOf(PlcTagTableGroup g)
        {
            var sys = g as PlcTagTableSystemGroup;
            if (sys != null) return sys.Groups;
            var usr = g as PlcTagTableUserGroup;
            return usr != null ? usr.Groups : null;
        }

        // ── 블록 ────────────────────────────────────────────────

        static void CollectBlocks(
            PlcBlockGroup group, string path, string rootName,
            List<object> result, List<object> inconsistent)
        {
            foreach (PlcBlock b in group.Blocks)
            {
                var d = Json.Obj();
                d["name"] = Try(() => b.Name);
                d["path"] = path;
                d["type"] = b.GetType().Name;   // OB / FB / FC / GlobalDB / InstanceDB / ArrayDB
                d["number"] = Try<object>(() => b.Number);
                d["language"] = Try(() => b.ProgrammingLanguage.ToString());
                d["namespace"] = Try(() => b.Namespace);
                d["memoryLayout"] = Try(() => b.MemoryLayout.ToString());
                d["autoNumber"] = Try<object>(() => b.AutoNumber);
                d["headerAuthor"] = Try(() => b.HeaderAuthor);
                d["headerFamily"] = Try(() => b.HeaderFamily);
                d["headerName"] = Try(() => b.HeaderName);
                d["headerVersion"] = Try(() => b.HeaderVersion == null ? null : b.HeaderVersion.ToString());
                d["isKnowHowProtected"] = Try<object>(() => b.IsKnowHowProtected);
                d["creationDate"] = Try<object>(() => b.CreationDate);
                d["modifiedDate"] = Try<object>(() => b.ModifiedDate);
                d["compileDate"] = Try<object>(() => b.CompileDate);
                d["codeModifiedDate"] = Try<object>(() => b.CodeModifiedDate);
                d["interfaceModifiedDate"] = Try<object>(() => b.InterfaceModifiedDate);
                d["parameterModified"] = Try<object>(() => b.ParameterModified);
                d["structureModified"] = Try<object>(() => b.StructureModified);
#if TIA_V21
                // V21 에서 PlcBlock 에 새로 생긴 것들 (V19/V20 에는 없다)
                d["title"] = Try(() => MlText.Read(b.Title));
                d["comment"] = Try(() => MlText.Read(b.Comment));
                d["downloadDate"] = Try<object>(() => b.DownloadDate);
                d["loadMemoryLength"] = Try<object>(() => b.LoadMemoryLength);
                d["workMemoryLength"] = Try<object>(() => b.WorkMemoryLength);
#endif
                bool? consistent = Try<bool?>(() => b.IsConsistent);
                d["isConsistent"] = consistent;
                result.Add(d);

                if (consistent == false)
                    inconsistent.Add(Bad(rootName, "block", path, Try(() => b.Name)));
            }

            foreach (PlcBlockGroup sub in group.Groups)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectBlocks(sub, subPath, rootName, result, inconsistent);
            }
        }

        // ── UDT ─────────────────────────────────────────────────

        static void CollectTypes(
            PlcTypeComposition types, PlcTypeUserGroupComposition groups,
            string path, string rootName, List<object> result, List<object> inconsistent)
        {
            foreach (PlcType t in types)
            {
                var d = Json.Obj();
                d["name"] = Try(() => t.Name);
                d["path"] = path;
                d["namespace"] = Try(() => t.Namespace);
                d["isKnowHowProtected"] = Try<object>(() => t.IsKnowHowProtected);
                d["creationDate"] = Try<object>(() => t.CreationDate);
                d["modifiedDate"] = Try<object>(() => t.ModifiedDate);
                d["interfaceModifiedDate"] = Try<object>(() => t.InterfaceModifiedDate);

                bool? consistent = Try<bool?>(() => t.IsConsistent);
                d["isConsistent"] = consistent;
                result.Add(d);

                if (consistent == false)
                    inconsistent.Add(Bad(rootName, "type", path, Try(() => t.Name)));
            }

            if (groups == null) return;
            foreach (PlcTypeUserGroup sub in groups)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectTypes(sub.Types, sub.Groups, subPath, rootName, result, inconsistent);
            }
        }

        // ── 태그 테이블 (여기는 Export 가 아예 필요 없다) ──────────

        static void CollectTagTables(
            PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups,
            string path, List<object> result, ref int tagCount)
        {
            foreach (PlcTagTable table in tables)
            {
                var tags = new List<object>();
                foreach (PlcTag tag in table.Tags)
                {
                    var t = Json.Obj();
                    t["name"] = Try(() => tag.Name);
                    t["dataType"] = Try(() => tag.DataTypeName);
                    t["address"] = Try(() => tag.LogicalAddress);
                    t["comment"] = Try(() => MlText.Read(tag.Comment));
                    t["externalAccessible"] = Try<object>(() => tag.ExternalAccessible);
                    t["externalVisible"] = Try<object>(() => tag.ExternalVisible);
                    t["externalWritable"] = Try<object>(() => tag.ExternalWritable);
                    t["isSafety"] = Try<object>(() => tag.IsSafety);
                    tags.Add(t);
                }

                var constants = new List<object>();
                try
                {
                    foreach (PlcUserConstant c in table.UserConstants)
                    {
                        var cd = Json.Obj();
                        cd["name"] = Try(() => c.Name);
                        cd["dataType"] = Try(() => c.DataTypeName);
                        cd["value"] = Try(() => c.Value);
                        cd["comment"] = Try(() => MlText.Read(c.Comment));
                        cd["scope"] = "user";
                        constants.Add(cd);
                    }
                }
                catch { }
                try
                {
                    foreach (PlcSystemConstant c in table.SystemConstants)
                    {
                        var cd = Json.Obj();
                        cd["name"] = Try(() => c.Name);
                        cd["dataType"] = Try(() => c.DataTypeName);
                        cd["value"] = Try(() => c.Value);
                        cd["scope"] = "system";
                        constants.Add(cd);
                    }
                }
                catch { }

                var d = Json.Obj();
                d["name"] = Try(() => table.Name);
                d["path"] = path;
                d["tagCount"] = tags.Count;
                d["tags"] = tags;
                d["constants"] = constants;
                result.Add(d);
                tagCount += tags.Count;
            }

            if (groups == null) return;
            foreach (PlcTagTableUserGroup sub in groups)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectTagTables(sub.TagTables, sub.Groups, subPath, result, ref tagCount);
            }
        }

        // ── 소프트웨어 유닛 ──────────────────────────────────────

        internal sealed class UnitRef
        {
            public string Kind;            // "unit" | "safetyUnit"
            public string Name;
            public string NamespacePreset;
            public string Author;
            public PlcBlockGroup BlockGroup;
            public PlcTypeSystemGroup TypeGroup;
            public PlcTagTableSystemGroup TagTableGroup;
        }

        public static List<UnitRef> EnumerateUnits(PlcSoftware plc)
        {
            var result = new List<UnitRef>();
            PlcUnitProvider provider;
            try { provider = ((IEngineeringServiceProvider)plc).GetService<PlcUnitProvider>(); }
            catch { return result; }
            if (provider == null) return result;   // 소프트웨어 유닛 미지원 CPU

            try
            {
                foreach (PlcUnit u in provider.UnitGroup.Units)
                    result.Add(new UnitRef
                    {
                        Kind = "unit",
                        Name = u.Name,
                        NamespacePreset = Try(() => u.NamespacePreset),
                        Author = Try(() => u.Author),
                        BlockGroup = u.BlockGroup,
                        TypeGroup = u.TypeGroup,
                        TagTableGroup = u.TagTableGroup
                    });
            }
            catch { }

            try
            {
                foreach (PlcSafetyUnit u in provider.UnitGroup.SafetyUnits)
                    result.Add(new UnitRef
                    {
                        Kind = "safetyUnit",
                        Name = u.Name,
                        NamespacePreset = Try(() => u.NamespacePreset),
                        Author = Try(() => u.Author),
                        BlockGroup = u.BlockGroup,
                        TypeGroup = u.TypeGroup,
                        TagTableGroup = u.TagTableGroup
                    });
            }
            catch { }

            return result;
        }

        static Dictionary<string, object> Bad(string root, string kind, string path, string name)
        {
            var d = Json.Obj();
            d["root"] = root;
            d["kind"] = kind;
            d["path"] = path;
            d["name"] = name;
            return d;
        }

        // know-how 보호 블록 등은 프로퍼티 읽기만으로도 던진다 — 하나가 죽어도
        // 나머지 덤프는 계속돼야 하므로 전부 감싼다.
        static T Try<T>(Func<T> f)
        {
            try { return f(); }
            catch { return default(T); }
        }
    }

    /// <summary>MultilingualText 를 {culture: text} 로 펴 준다.</summary>
    internal static class MlText
    {
        public static Dictionary<string, object> Read(MultilingualText mt)
        {
            if (mt == null) return null;
            var d = Json.Obj();
            try
            {
                foreach (MultilingualTextItem item in mt.Items)
                {
                    string culture = item.Language != null && item.Language.Culture != null
                        ? item.Language.Culture.Name
                        : "?";
                    d[culture] = item.Text;
                }
            }
            catch { }
            return d.Count == 0 ? null : d;
        }
    }
}
