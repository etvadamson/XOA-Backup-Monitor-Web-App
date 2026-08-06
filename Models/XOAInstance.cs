namespace XOABackupMonitorWeb.Models
{
    public class XOAInstance
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string ApiToken { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
    }

    public class XOAInstanceSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool HasToken { get; set; }
    }
}
