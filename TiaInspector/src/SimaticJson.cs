using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaInspector
{
    /// <summary>
    /// Export 된 SimaticML XML 을 정규화된 JSON 으로 옮긴다.
    ///
    /// 그냥 XML 을 1:1 로 뒤집으면 SimaticML 특유의 AttributeList/ObjectList
    /// 래핑이 그대로 남아 쓰기 어렵다. 여기서는 소비자가 바로 쓸 수 있는 모양
    /// (인터페이스 섹션 → 멤버 배열, 코멘트 → {culture: text}, SCL → 복원된
    /// 소스 텍스트) 으로 편다. 원본 충실도가 필요하면 나란히 저장되는 XML 을
    /// 보면 된다 — 그래서 여기서 손실을 감수해도 된다.
    ///
    /// 단 하나 예외가 LAD/FBD 다. 그래픽 로직은 정규화할 표준형이 없어서
    /// FlgNet 을 구조 그대로(generic) 실어 보낸다.
    /// </summary>
    internal static class SimaticJson
    {
        const string InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

        public static Dictionary<string, object> ConvertFile(string xmlPath)
        {
            XDocument doc = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            return Convert(doc);
        }

        public static Dictionary<string, object> Convert(XDocument doc)
        {
            var result = Json.Obj();
            XElement docRoot = doc.Root;
            if (docRoot == null) return result;

            XElement eng = docRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "Engineering");
            if (eng != null) result["engineeringVersion"] = (string)eng.Attribute("version");

            // <Document> 아래 Engineering/DocumentInfo 를 뺀 첫 요소가 본체다.
            XElement obj = docRoot.Elements().FirstOrDefault(e =>
                e.Name.LocalName != "Engineering" && e.Name.LocalName != "DocumentInfo");
            if (obj == null) return result;

            result["kind"] = obj.Name.LocalName;   // SW.Blocks.OB, SW.Types.PlcStruct, SW.Tags.PlcTagTable ...
            ReadObject(obj, result);
            return result;
        }

        // ── 객체 한 개 (AttributeList + ObjectList) ────────────────

        static void ReadObject(XElement obj, Dictionary<string, object> target)
        {
            var attributes = Json.Obj();

            XElement attrList = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            if (attrList != null)
            {
                foreach (XElement a in attrList.Elements())
                {
                    string name = a.Name.LocalName;

                    if (name == "Interface")
                    {
                        target["interface"] = ReadInterface(a);
                        continue;
                    }
                    if (name == "NetworkSource")
                    {
                        target["networkSource"] = ReadNetworkSource(a);
                        continue;
                    }
                    if (name == "StartValue" || name == "Value")
                    {
                        attributes[name] = a.Value;
                        continue;
                    }

                    // 자식이 있으면 구조를 살려서, 없으면 스칼라로
                    attributes[name] = a.HasElements ? (object)Generic(a) : a.Value;
                }
            }

            // 자주 쓰는 것들은 최상위로 끌어올린다
            Promote(attributes, target, "Name", "name");
            Promote(attributes, target, "Number", "number");
            Promote(attributes, target, "ProgrammingLanguage", "language");
            Promote(attributes, target, "Namespace", "namespace");
            Promote(attributes, target, "HeaderVersion", "headerVersion");
            Promote(attributes, target, "HeaderAuthor", "headerAuthor");
            Promote(attributes, target, "HeaderFamily", "headerFamily");
            Promote(attributes, target, "HeaderName", "headerName");
            Promote(attributes, target, "DataTypeName", "dataType");
            Promote(attributes, target, "LogicalAddress", "address");
            Promote(attributes, target, "SecondaryType", "secondaryType");

            if (attributes.Count > 0) target["attributes"] = attributes;

            XElement objList = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
            if (objList == null) return;

            var networks = new List<object>();
            var children = new List<object>();

            foreach (XElement child in objList.Elements())
            {
                string kind = child.Name.LocalName;
                string composition = (string)child.Attribute("CompositionName");

                if (kind == "MultilingualText")
                {
                    // CompositionName 이 Comment / Title 을 가른다
                    string key = string.IsNullOrEmpty(composition)
                        ? "text"
                        : char.ToLowerInvariant(composition[0]) + composition.Substring(1);
                    target[key] = ReadMultilingual(child);
                    continue;
                }

                if (kind == "SW.Blocks.CompileUnit")
                {
                    var net = Json.Obj();
                    net["index"] = networks.Count;
                    ReadObject(child, net);
                    networks.Add(net);
                    continue;
                }

                // PlcTag / PlcUserConstant / PlcSystemConstant / Member 등
                var c = Json.Obj();
                c["kind"] = kind;
                if (!string.IsNullOrEmpty(composition)) c["composition"] = composition;
                ReadObject(child, c);
                children.Add(c);
            }

            if (networks.Count > 0) target["networks"] = networks;
            if (children.Count > 0) target["children"] = children;
        }

        static void Promote(
            Dictionary<string, object> from, Dictionary<string, object> to,
            string srcKey, string dstKey)
        {
            object v;
            if (!from.TryGetValue(srcKey, out v)) return;
            if (v is string s && s.Length == 0) { from.Remove(srcKey); return; }
            to[dstKey] = v;
            from.Remove(srcKey);
        }

        // ── 인터페이스 (Input/Output/InOut/Static/Temp/Constant/Return) ──

        static Dictionary<string, object> ReadInterface(XElement interfaceEl)
        {
            var result = Json.Obj();
            XElement sections = interfaceEl.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Sections");
            if (sections == null) return result;

            foreach (XElement section in sections.Elements().Where(e => e.Name.LocalName == "Section"))
            {
                string name = (string)section.Attribute("Name") ?? "?";
                result[name] = ReadMembers(section);
            }
            return result;
        }

        static List<object> ReadMembers(XElement parent)
        {
            var list = new List<object>();
            foreach (XElement m in parent.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                var d = Json.Obj();
                d["name"] = (string)m.Attribute("Name");
                d["datatype"] = (string)m.Attribute("Datatype");

                string accessibility = (string)m.Attribute("Accessibility");
                if (accessibility != null) d["accessibility"] = accessibility;
                string remanence = (string)m.Attribute("Remanence");
                if (remanence != null) d["remanence"] = remanence;
                if ((string)m.Attribute("Informative") == "true") d["informative"] = true;

                XElement startValue = m.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue");
                if (startValue != null) d["startValue"] = startValue.Value;

                XElement comment = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment");
                if (comment != null) d["comment"] = ReadMultiLanguageText(comment);

                // Member 의 AttributeList 는 ExternalAccessible/SetPoint 같은 플래그다
                XElement attrs = m.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                if (attrs != null)
                {
                    var flags = Json.Obj();
                    foreach (XElement a in attrs.Elements())
                    {
                        string key = (string)a.Attribute("Name") ?? a.Name.LocalName;
                        flags[key] = a.Value == "true" ? (object)true
                                   : a.Value == "false" ? (object)false
                                   : a.Value;
                    }
                    if (flags.Count > 0) d["flags"] = flags;
                }

                // Struct 는 Member 가 중첩된다
                var nested = ReadMembers(m);
                if (nested.Count > 0) d["members"] = nested;

                list.Add(d);
            }
            return list;
        }

        // ── 네트워크 소스 ──────────────────────────────────────

        static Dictionary<string, object> ReadNetworkSource(XElement networkSource)
        {
            var d = Json.Obj();
            XElement body = networkSource.Elements().FirstOrDefault();
            if (body == null) return d;

            string kind = body.Name.LocalName;
            d["form"] = kind;

            if (kind == "StructuredText")
            {
                // 토큰 스트림을 SCL 소스로 되돌린다 — 사람이 읽을 수 있어야
                // 의미가 있다.
                d["scl"] = ReconstructScl(body);
                return d;
            }

            // FlgNet(LAD/FBD/GRAPH) — 정규형이 없으니 구조 그대로 싣는다
            d["graph"] = Generic(body);
            return d;
        }

        /// <summary>
        /// StructuredText 토큰 스트림 → SCL 텍스트.
        /// Token/Blank/NewLine/Access/LineComment/Comment 로 이루어져 있고,
        /// Access 는 Symbol(Component + Token) 또는 Constant 를 품는다.
        /// </summary>
        public static string ReconstructScl(XElement st)
        {
            var sb = new StringBuilder();
            foreach (XElement e in st.Elements()) AppendSclNode(sb, e);
            return sb.ToString();
        }

        static void AppendSclNode(StringBuilder sb, XElement e)
        {
            switch (e.Name.LocalName)
            {
                case "Token":
                    sb.Append((string)e.Attribute("Text"));
                    break;

                case "Blank":
                    sb.Append(' ', NumOf(e, 1));
                    break;

                case "NewLine":
                    sb.Append('\n', NumOf(e, 1));
                    break;

                case "LineComment":
                    sb.Append("// ").Append(InnerText(e));
                    break;

                case "Comment":
                    sb.Append("(* ").Append(InnerText(e)).Append(" *)");
                    break;

                case "Access":
                case "Symbol":
                    foreach (XElement child in e.Elements()) AppendSclNode(sb, child);
                    break;

                case "Component":
                    sb.Append((string)e.Attribute("Name"));
                    // 배열 인덱스는 Component 아래 Access 로 들어온다
                    foreach (XElement child in e.Elements()) AppendSclNode(sb, child);
                    break;

                case "Constant":
                    // 리터럴은 <ConstantValue> 자식으로, 이름 있는 상수
                    // (Scope="GlobalConstant"/"TypedConstant")는 Name 속성으로
                    // 온다 — 후자를 놓치면 소스에 빈칸이 남는다.
                    XElement value = e.Elements()
                        .FirstOrDefault(x => x.Name.LocalName == "ConstantValue");
                    if (value != null) sb.Append(value.Value);
                    else sb.Append((string)e.Attribute("Name"));
                    break;

                default:
                    foreach (XElement child in e.Elements()) AppendSclNode(sb, child);
                    break;
            }
        }

        static int NumOf(XElement e, int fallback)
        {
            int n;
            return int.TryParse((string)e.Attribute("Num"), out n) ? n : fallback;
        }

        static string InnerText(XElement e)
        {
            XElement text = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Text");
            return text != null ? text.Value : e.Value;
        }

        // ── 다국어 텍스트 ──────────────────────────────────────

        /// <summary>SW 객체 쪽 모양: MultilingualText > MultilingualTextItem > Culture/Text</summary>
        static Dictionary<string, object> ReadMultilingual(XElement mt)
        {
            var d = Json.Obj();
            foreach (XElement item in mt.Descendants().Where(e => e.Name.LocalName == "MultilingualTextItem"))
            {
                XElement attrs = item.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                if (attrs == null) continue;
                string culture = ValueOf(attrs, "Culture") ?? "?";
                d[culture] = ValueOf(attrs, "Text") ?? "";
            }
            return d;
        }

        /// <summary>인터페이스 쪽 모양: Comment > MultiLanguageText Lang="en-US"</summary>
        static Dictionary<string, object> ReadMultiLanguageText(XElement comment)
        {
            var d = Json.Obj();
            foreach (XElement t in comment.Elements().Where(e => e.Name.LocalName == "MultiLanguageText"))
                d[(string)t.Attribute("Lang") ?? "?"] = t.Value;
            return d;
        }

        static string ValueOf(XElement parent, string localName)
        {
            XElement e = parent.Elements().FirstOrDefault(x => x.Name.LocalName == localName);
            return e != null ? e.Value : null;
        }

        // ── 무손실 generic 변환 (FlgNet 전용) ───────────────────

        static Dictionary<string, object> Generic(XElement e)
        {
            var d = Json.Obj();
            d["name"] = e.Name.LocalName;

            if (e.HasAttributes)
            {
                var attrs = Json.Obj();
                foreach (XAttribute a in e.Attributes())
                    if (!a.IsNamespaceDeclaration) attrs[a.Name.LocalName] = a.Value;
                if (attrs.Count > 0) d["attrs"] = attrs;
            }

            if (e.HasElements)
            {
                var children = new List<object>();
                foreach (XElement c in e.Elements()) children.Add(Generic(c));
                d["children"] = children;
            }
            else
            {
                string text = e.Value;
                if (!string.IsNullOrEmpty(text) && text.Trim().Length > 0) d["text"] = text;
            }

            return d;
        }
    }
}
