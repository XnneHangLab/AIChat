using System;

namespace AIChat.Core
{
    /// <summary>
    /// OpenAI ChatCompletion 響應格式（簡化版）
    /// 用於解析 XnneHangLab Chat Server 的返回
    /// 所有類都標記 [Serializable] 以支持 Unity JsonUtility
    /// </summary>
    [Serializable]
    public class OpenAIResponse
    {
        public string id;
        public string object_type;  // JSON 中是 "object"，但 object 是 C# 關鍵字
        public long created;
        public string model;
        public Choice[] choices;
        public Usage usage;
    }

    [Serializable]
    public class Choice
    {
        public int index;
        public Message message;
        public string finish_reason;
    }

    [Serializable]
    public class Message
    {
        public string role;
        public string content;
    }

    [Serializable]
    public class Usage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }
}
