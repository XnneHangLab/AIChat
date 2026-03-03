using AIChat.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIChat.Core
{
    public static class ResponseParser
    {
        public static string EscapeJson(string s)
        {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") ?? "";
        }

        public static string ExtractContentFromOllama(string jsonResponse)
        {
            try
            {
                var match = Regex.Match(jsonResponse, "\"content\"\\s*:\\s*\"([^\"]*)\"");
                if (match.Success)
                {
                    return Regex.Unescape(match.Groups[1].Value);
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"[Ollama] 解析失败: {ex.Message}");
                return null;
            }
        }

        public static string ExtractContentRegex(string json)
        {
            try { var match = Regex.Match(json, "\"content\"\\s*:\\s*\"(.*?)\""); return match.Success ? Regex.Unescape(match.Groups[1].Value) : null; }
            catch { return null; }
        }
        // 简易 JSON 提取辅助函数
        public static string ExtractJsonValue(string json, string key)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"(.*?)\"");
            return match.Success ? Regex.Unescape(match.Groups[1].Value) : "";
        }
        // =========================================================================================
        // 【新增辅助函数】确保对话文本（字幕）强制换行，以防过长溢出屏幕。
        // =========================================================================================
        /// <summary>
        /// 在长文本中插入换行符，以确保文本在 UI 中可见。
        /// </summary>
        /// <param name="text">原始文本</param>
        /// <param name="maxLineLength">每行最大字符数</param>
        /// <returns>带有换行符的文本</returns>
        public static string InsertLineBreaks(string text, int maxLineLength = 25)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLineLength)
            {
                return text;
            }

            StringBuilder sb = new StringBuilder();
            int currentLength = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                sb.Append(c);
                currentLength++;

                if (currentLength >= maxLineLength && c != '\n')
                {
                    // 检查下一个字符是否已经是换行符，避免双重换行
                    if (i + 1 < text.Length && text[i + 1] != '\n')
                    {
                        sb.Append('\n');
                        currentLength = 0;
                    }
                }

                if (c == '\n')
                {
                    currentLength = 0;
                }
            }
            return sb.ToString();
        }

        // =========================================================================================
        // 【新增】按中文标点断句（用于流式 TTS）
        // =========================================================================================
        /// <summary>
        /// 按中文标点（。！？！）分割文本为句子数组
        /// </summary>
        /// <param name="text">原始文本</param>
        /// <returns>句子数组</returns>
        /// <summary>
        /// 剥离 [Emotion] ||| 前缀，返回纯文本内容
        /// 防止 [Emotion] 和 ||| 被送进 TTS/DeepLX 产生怪声
        /// </summary>
        public static string StripEmotionPrefix(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 匹配 [xxx] 后跟可选的 ||| 分隔符
            var match = System.Text.RegularExpressions.Regex.Match(
                text, @"^\[[^\]]+\]\s*(?:\|{1,3}\s*)?(.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Trim() : text;
        }

        public static string[] SplitByChinesePunctuation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];

            // ── 方案 B：先按 \n 分段，再按标点细切，短段归并 ──
            const int MergeThreshold = 12; // 少于这个字数的段归并到上一句

            // Step 1：按换行粗切成段落
            string[] paragraphs = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Step 2：每段再按标点细切
            char[] punctuation = new char[] { '。', '？', '！', '!' };
            var rawSentences = new List<string>();
            foreach (var para in paragraphs)
            {
                string trimmedPara = para.Trim();
                if (string.IsNullOrEmpty(trimmedPara)) continue;

                string[] parts = trimmedPara.Split(punctuation, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    string s = part.Trim();
                    if (!string.IsNullOrEmpty(s))
                        rawSentences.Add(s);
                }
            }

            // Step 3：归并规则
            // - 少于 MergeThreshold 字的短段（如 ——莎士比亚）归并到上一句
            // - 上一句以引号闭合符结尾（」』"'），当前段是说话人标注，归并到上一句
            var result = new List<string>();
            foreach (var s in rawSentences)
            {
                bool prevEndsWithQuote = result.Count > 0 &&
                    Regex.IsMatch(result[result.Count - 1], @"[」』"']$");
                bool shouldMerge = result.Count > 0 && (
                    s.Length < MergeThreshold ||
                    prevEndsWithQuote
                );
                if (shouldMerge)
                    result[result.Count - 1] = result[result.Count - 1] + s;
                else
                    result.Add(s);
            }

            return result.ToArray();
        }
    }
}

