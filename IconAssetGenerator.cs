using System;

namespace PortBridge {
    static class IconAssetGenerator {
        [STAThread]
        static int Main(string[] args) {
            if (args.Length != 1) return 2;
            IconArtwork.WriteIconSet(args[0]);
            return 0;
        }
    }
}
