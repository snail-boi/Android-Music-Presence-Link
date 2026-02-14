using System;
using System.Runtime.InteropServices;

namespace musicpresense
{
    internal static class ScrcpyVolumeController
    {
        public static bool TryAdjustVolume(int processId, float delta)
        {
            if (processId <= 0) return false;

            IMMDeviceEnumerator? deviceEnumerator = null;
            IMMDevice? device = null;
            IAudioSessionManager2? sessionManager = null;
            IAudioSessionEnumerator? sessionEnumerator = null;

            try
            {
                deviceEnumerator = new MMDeviceEnumerator() as IMMDeviceEnumerator;
                if (deviceEnumerator == null) return false;

                Marshal.ThrowExceptionForHR(deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));
                if (device == null) return false;

                var iid = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0, IntPtr.Zero, out var managerObj));
                sessionManager = managerObj as IAudioSessionManager2;
                if (sessionManager == null) return false;

                Marshal.ThrowExceptionForHR(sessionManager.GetSessionEnumerator(out sessionEnumerator));
                if (sessionEnumerator == null) return false;

                Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out int count));

                for (int i = 0; i < count; i++)
                {
                    Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(i, out var sessionControl));
                    if (sessionControl == null) continue;

                    try
                    {
                        if (sessionControl is not IAudioSessionControl2 sessionControl2)
                            continue;

                        sessionControl2.GetProcessId(out int sessionPid);
                        if (sessionPid != processId)
                            continue;

                        if (sessionControl2 is ISimpleAudioVolume volumeControl)
                        {
                            volumeControl.GetMasterVolume(out float current);
                            float next = Math.Clamp(current + delta, 0f, 1f);
                            volumeControl.SetMasterVolume(next, Guid.Empty);
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessionControl);
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (sessionEnumerator != null) Marshal.ReleaseComObject(sessionEnumerator);
                if (sessionManager != null) Marshal.ReleaseComObject(sessionManager);
                if (device != null) Marshal.ReleaseComObject(device);
                if (deviceEnumerator != null) Marshal.ReleaseComObject(deviceEnumerator);
            }
        }

        private enum EDataFlow
        {
            eRender,
            eCapture,
            eAll
        }

        private enum ERole
        {
            eConsole,
            eMultimedia,
            eCommunications
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out object devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
            int GetDevice(string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            int GetId(out string ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(ref Guid AudioSessionGuid, int StreamFlags, out IAudioSessionControl SessionControl);
            int GetSimpleAudioVolume(ref Guid AudioSessionGuid, int StreamFlags, out ISimpleAudioVolume AudioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
            int RegisterSessionNotification(IntPtr SessionNotification);
            int UnregisterSessionNotification(IntPtr SessionNotification);
            int RegisterDuckNotification(string sessionID, IntPtr duckNotification);
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int SessionCount);
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out int pRetVal);
            int GetDisplayName(out string pRetVal);
            int SetDisplayName(string Value, Guid EventContext);
            int GetIconPath(out string pRetVal);
            int SetIconPath(string Value, Guid EventContext);
            int GetGroupingParam(out Guid pRetVal);
            int SetGroupingParam(Guid Override, Guid EventContext);
            int RegisterAudioSessionNotification(IntPtr NewNotifications);
            int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            int GetState(out int pRetVal);
            int GetDisplayName(out string pRetVal);
            int SetDisplayName(string Value, Guid EventContext);
            int GetIconPath(out string pRetVal);
            int SetIconPath(string Value, Guid EventContext);
            int GetGroupingParam(out Guid pRetVal);
            int SetGroupingParam(Guid Override, Guid EventContext);
            int RegisterAudioSessionNotification(IntPtr NewNotifications);
            int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            int GetSessionIdentifier(out string pRetVal);
            int GetSessionInstanceIdentifier(out string pRetVal);
            int GetProcessId(out int pRetVal);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }

        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float fLevel, Guid EventContext);
            int GetMasterVolume(out float pfLevel);
            int SetMute(bool bMute, Guid EventContext);
            int GetMute(out bool pbMute);
        }
    }
}
