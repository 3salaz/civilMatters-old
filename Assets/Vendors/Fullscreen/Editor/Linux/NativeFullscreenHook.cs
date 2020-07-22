using UnityEditor;

namespace FullscreenEditor.Linux {
    internal static class NativeFullscreenHooks {

        [InitializeOnLoadMethod]
        private static void Init() {
            if (!FullscreenUtility.IsLinux)
                return;

            FullscreenCallbacks.afterFullscreenOpen += (fs) =>
                wmctrl.SetNativeFullscreen(true, fs.m_dst.Container);
            FullscreenCallbacks.beforeFullscreenClose += (fs) =>
                wmctrl.SetNativeFullscreen(false, fs.m_dst.Container);
        }

    }
}
