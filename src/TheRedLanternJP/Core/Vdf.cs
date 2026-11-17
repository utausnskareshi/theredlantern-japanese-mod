using System;
using System.Collections.Generic;
using System.Text;

namespace TheRedLanternJP.Core
{
    /// <summary>Steam の VDF / ACF（"キー" "値" と { } の入れ子）を読むための最小限のパーサー。</summary>
    internal sealed class VdfNode
    {
        public string Key;

        /// <summary>葉ノードの値。子を持つノードでは null。</summary>
        public string Value;

        /// <summary>子ノード。葉ノードでは null。</summary>
        public List<VdfNode> Children;

        public VdfNode Child(string key)
        {
            if (Children == null) return null;
            foreach (var c in Children)
            {
                if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        public string ValueOf(string key)
        {
            var c = Child(key);
            return c == null ? null : c.Value;
        }

        /// <summary>テキスト全体を読み、最上位の組を子に持つ仮のルートノードを返す。</summary>
        public static VdfNode Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int pos = 0;
            var root = new VdfNode { Key = string.Empty, Children = new List<VdfNode>() };
            ParseChildren(text, ref pos, root, 0);
            return root;
        }

        /// <summary>入れ子の深さの上限。Steam のファイルは数段しかない。壊れたファイルでスタックを使い切らないための歯止め。</summary>
        internal const int MaxDepth = 64;

        private static void ParseChildren(string s, ref int pos, VdfNode parent, int depth)
        {
            bool topLevel = depth == 0;
            if (depth > MaxDepth) throw new FormatException("VDF: 入れ子が深すぎます。");
            while (true)
            {
                bool quoted;
                string token = NextToken(s, ref pos, out quoted);
                if (token == null)
                {
                    if (!topLevel) throw new FormatException("VDF: 閉じ括弧 '}' がありません。");
                    return;
                }
                if (!quoted && token == "}")
                {
                    if (topLevel) throw new FormatException("VDF: 対応しない '}' があります。");
                    return;
                }
                if (!quoted && token == "{") throw new FormatException("VDF: キーの無い '{' があります。");

                var node = new VdfNode { Key = token };
                bool valueQuoted;
                string value = NextToken(s, ref pos, out valueQuoted);
                if (value == null) throw new FormatException("VDF: キー \"" + token + "\" に値がありません。");
                if (!valueQuoted && value == "{")
                {
                    node.Children = new List<VdfNode>();
                    ParseChildren(s, ref pos, node, depth + 1);
                }
                else if (!valueQuoted && value == "}")
                {
                    throw new FormatException("VDF: キー \"" + token + "\" に値がありません。");
                }
                else
                {
                    node.Value = value;
                }
                parent.Children.Add(node);
            }
        }

        private static string NextToken(string s, ref int pos, out bool quoted)
        {
            quoted = false;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (char.IsWhiteSpace(c))
                {
                    pos++;
                }
                else if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
                {
                    while (pos < s.Length && s[pos] != '\n') pos++;
                }
                else
                {
                    break;
                }
            }
            if (pos >= s.Length) return null;

            char first = s[pos];
            if (first == '{' || first == '}')
            {
                pos++;
                return first.ToString();
            }

            var sb = new StringBuilder();
            if (first == '"')
            {
                quoted = true;
                pos++;
                while (true)
                {
                    if (pos >= s.Length) throw new FormatException("VDF: 文字列が閉じられていません。");
                    char c = s[pos++];
                    if (c == '"') break;
                    if (c == '\\' && pos < s.Length)
                    {
                        char e = s[pos++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            default: sb.Append('\\').Append(e); break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}' && s[pos] != '"')
            {
                sb.Append(s[pos++]);
            }
            return sb.ToString();
        }
    }
}
