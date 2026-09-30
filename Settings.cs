using System;
using System.IO;
using System.Net;
using System.Xml;
using System.Xml.Serialization;
using System.Linq;
namespace PortBridge {
    public class Settings {
        public string ListenAddress = "127.0.0.1";
        public int ListenPort = 7890;
        public string ListenPorts;
        public bool ShouldSerializeListenPort() { return ListenPorts == null; }
        public int[] GetListenPorts() {
            string text = (ListenPorts ?? ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture)).Trim().Replace('，', ',');
            // A user removing the last port from "7890,7891" often leaves "7890,".
            // Treat one trailing separator as unfinished typing and save the remaining ports.
            if (text.EndsWith(",", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1).TrimEnd();
            string[] parts = text.Split(',');
            if (parts.Length > 64) throw new ArgumentException("最多同时监听 64 个端口。");
            var ports = new System.Collections.Generic.List<int>();
            foreach (string part in parts) {
                int port;
                if (!Int32.TryParse(part.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    throw new ArgumentException("监听端口须为 1–65535，用逗号分隔，例如 7890,7891,7892；不能有空项。");
                if (ports.Contains(port)) throw new ArgumentException("监听端口重复：" + port + "。");
                ports.Add(port);
            }
            return ports.ToArray();
        }
        public string PortsText() { return String.Join(",", GetListenPorts().Select(p => p.ToString()).ToArray()); }
        public string TargetAddress = "127.0.0.1";
        public int TargetPort = 23578;
        public bool Tcp = true;
        public bool Udp = true;
        public bool AutoRelay = false;
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortBridge", "settings.xml"); } }
        public static Settings Load() { return Read(FilePath); }
        public static Settings Read(string path) {
            if (new FileInfo(path).Length > 65536) throw new ArgumentException("配置文件不能超过 64 KB。");
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) {
                var value = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(reader); value.Validate(); return value;
            }
        }
        public void Validate() {
            int[] ports = GetListenPorts();
            if (TargetPort < 1 || TargetPort > 65535) throw new ArgumentException("端口范围为 1–65535。");
            IPAddress bind = ParseAddress(ListenAddress), target = ParseAddress(TargetAddress);
            if (target.Equals(IPAddress.Any) || target.Equals(IPAddress.IPv6Any)) throw new ArgumentException("目标地址不能为 0.0.0.0 或 ::。");
            if (!Tcp && !Udp) throw new ArgumentException("请至少选择 TCP 或 UDP。");
            if (ports.Contains(TargetPort) && (bind.Equals(target) || ((bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any)) && IPAddress.IsLoopback(target)))) throw new ArgumentException("监听和目标不能指向同一端口。");
        }
        static IPAddress ParseAddress(string text) { IPAddress value; if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("IP 地址不能为空。"); if (text.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback; if (!IPAddress.TryParse(text.Trim(), out value)) throw new ArgumentException("请输入有效 IP 地址或 localhost。"); return value; }
        public void Save() {
            SaveTo(FilePath);
        }
        public void SaveTo(string path) {
            Validate(); path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var f = File.Create(temp)) new XmlSerializer(typeof(Settings)).Serialize(f, this);
                if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
