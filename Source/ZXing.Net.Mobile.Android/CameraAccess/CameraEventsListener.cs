using System;
using System.Threading.Tasks;
using Android.Hardware;
using Android.OS;
using Android.Util;
using ApxLabs.FastAndroidCamera;

namespace ZXing.Mobile.CameraAccess
{
    public class CameraEventsListener : Java.Lang.Object, INonMarshalingPreviewCallback, Camera.IAutoFocusCallback
    {
        public event EventHandler<FastJavaByteArray> OnPreviewFrameReady; 

        //public void OnPreviewFrame(byte[] data, Camera camera)
        //{
        //    OnPreviewFrameReady?.Invoke(this, data);            
        //}
        
        public void OnPreviewFrame(IntPtr data, Camera camera)
        {
			using (var fastArray = new FastJavaByteArray(data))
			{
				OnPreviewFrameReady?.Invoke(this, fastArray);

				camera.AddCallbackBuffer(fastArray);
			}
        }

        public async void OnAutoFocus(bool success, Camera camera)
        {
            Android.Util.Log.Debug(MobileBarcodeScanner.TAG, "callback AutoFocus {0}", success ? "Succeeded" : "Failed");
            camera.CancelAutoFocus();
        }
    }
}