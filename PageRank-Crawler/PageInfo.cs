namespace PageRank_Crawler
{
    public class PageInfo
    {
        public string Url { get; set; }

        public int StatusCode { get; set; } = 0;

        public float PageRank { get; set; }

        public float LayoutDuration { get; set; }

        public float RecalcStyleDuration { get; set; }

        public float ScriptDuration { get; set; }

        public float TaskDuration { get; set; }

        public long JSHeapUsedSize { get; set; }

        public long JSHeapTotalSize { get; set; }

        public long Nodes { get; set; }

        public long JSEventListeners { get; set; }
    }
}
