/*
* Copyright 2018 ZXing/Redth - https://github.com/Redth/ZXing.Net.Mobile
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
*      http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
* 
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using Android.Views;
using ApxLabs.FastAndroidCamera;
using Camera = Android.Hardware.Camera;
using Android.Util;

namespace ZXing.Mobile.CameraAccess
{
    public class CameraController
    {
        private const float MAX_EXPOSURE_COMPENSATION = 1.5f;
        private const float MIN_EXPOSURE_COMPENSATION = 0.0f;
        private const int MIN_FPS = 10;
        private const int MAX_FPS = 20;
        private const int AREA_PER_1000 = 800;
        private readonly Context _context;
        private readonly ISurfaceHolder _holder;
        private readonly SurfaceView _surfaceView;
        private readonly CameraEventsListener _cameraEventListener;
        private int _cameraId;
        private bool _continuousAutofocus;
        private CancellationTokenSource cts;
        IScannerSessionHost _scannerHost;
        

        public CameraController(SurfaceView surfaceView, CameraEventsListener cameraEventListener, IScannerSessionHost scannerHost)
        {
            _context = surfaceView.Context;
            _holder = surfaceView.Holder;
            _surfaceView = surfaceView;
            _cameraEventListener = cameraEventListener;
            _scannerHost = scannerHost;
        }

        public Camera Camera { get; private set; }

        public int LastCameraDisplayOrientationDegree { get; private set; }

        public SurfaceOrientation LastDisplayOrientation { get; private set; }

        public void RefreshCamera()
        {
            if (_holder == null) return;

            ApplyCameraSettings();

            try
            {
                Camera.SetPreviewDisplay(_holder);
                Camera.StartPreview();
            }
            catch (Exception ex)
            {
                Log.Debug(MobileBarcodeScanner.TAG, ex.ToString());
            }
        }

        public void SetupCamera()
        {
            if (Camera != null) return;

            ZXing.Net.Mobile.Android.PermissionsHandler.CheckCameraPermissions(_context);

            var perf = PerformanceCounter.Start();
            OpenCamera();
            PerformanceCounter.Stop(perf, "Setup Camera took {0}ms");

            if (Camera == null) return;

            perf = PerformanceCounter.Start();
            ApplyCameraSettings();

            try
            {
                Camera.SetPreviewDisplay(_holder);

                var previewParameters = Camera.GetParameters();
                var previewSize = previewParameters.PreviewSize;
                var bitsPerPixel = ImageFormat.GetBitsPerPixel(previewParameters.PreviewFormat);

                int bufferSize = (previewSize.Width * previewSize.Height * bitsPerPixel) / 8;

                Log.Debug(MobileBarcodeScanner.TAG, $"bitsPerPixed={bitsPerPixel}; bufferSize={bufferSize}");
                const int NUM_PREVIEW_BUFFERS = 5;
                for (uint i = 0; i < NUM_PREVIEW_BUFFERS; ++i)
                {
                    using (var buffer = new FastJavaByteArray(bufferSize))
                        Camera.AddCallbackBuffer(buffer);
                }

                Camera.StartPreview();

                Camera.SetNonMarshalingPreviewCallback(_cameraEventListener);                
            }
            catch (Exception ex)
            {
                Log.Debug(MobileBarcodeScanner.TAG, ex.ToString());
                return;
            }
            finally
            {
                PerformanceCounter.Stop(perf, "Setup Camera Parameters took {0}ms");
            }

            // Docs suggest if Auto or Macro modes, we should invoke AutoFocus at least once
            var currentFocusMode = Camera.GetParameters().FocusMode;
            if (currentFocusMode == Camera.Parameters.FocusModeAuto
                || currentFocusMode == Camera.Parameters.FocusModeMacro)
                AutoFocus();
            
        }

        public void AutoFocus()
        {
            AutoFocus(0, 0, false);
        }

        public void AutoFocus(int x, int y)
        {
            // The bounds for focus areas are actually -1000 to 1000
            // So we need to translate the touch coordinates to this scale
            var focusX = x / _surfaceView.Width * 2000 - 1000;
            var focusY = y / _surfaceView.Height * 2000 - 1000;

            // Call the autofocus with our coords
            AutoFocus(focusX, focusY, true);
        }

        public void ShutdownCamera()
        {
            cts?.Cancel();
            cts?.Dispose();
            if (Camera == null) return;

            // camera release logic takes about 0.005 sec so there is no need in async releasing
            var perf = PerformanceCounter.Start();
            try
            {
                try
                {
                    Camera.StopPreview();
                    Camera.SetNonMarshalingPreviewCallback(null);

                    //Camera.SetPreviewCallback(null);

                    Log.Debug(MobileBarcodeScanner.TAG, $"Calling SetPreviewDisplay: null");
                    Camera.SetPreviewDisplay(null);
                }
                catch (Exception ex)
                {
                    Log.Error(MobileBarcodeScanner.TAG, ex.ToString());
                }
                Camera.Release();
                Camera = null;
            }
            catch (Exception e)
            {
                Log.Error(MobileBarcodeScanner.TAG, e.ToString());
            }

            PerformanceCounter.Stop(perf, "Shutdown camera took {0}ms");
        }

        public void SetBestExposure(Camera.Parameters parameters, bool lightOn)
        {
            int   minExposure = parameters.MinExposureCompensation;
            int   maxExposure = parameters.MaxExposureCompensation;
            float step        = parameters.ExposureCompensationStep;
            if ((minExposure != 0 || maxExposure != 0) && step > 0.0f)
            {
                // Set low when light is on
                float targetCompensation = lightOn ? MIN_EXPOSURE_COMPENSATION : MAX_EXPOSURE_COMPENSATION;
                int   compensationSteps  = (int)(targetCompensation / step);
                float actualCompensation = step * compensationSteps;
                // Clamp value:
                compensationSteps = Math.Max(Math.Min(compensationSteps, maxExposure), minExposure);
                if (parameters.ExposureCompensation == compensationSteps)
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "FLASH on: " + lightOn + " - Exposure compensation already set to " + compensationSteps + " / " + actualCompensation);
                }
                else
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "FLASH on: " + lightOn + " - Setting exposure compensation to " + compensationSteps + " / " + actualCompensation);
                }
            }
            else
            {
                Log.Debug(MobileBarcodeScanner.TAG, "Camera does not support exposure compensation");
            }
        }
        private void OpenCamera()
        {
            try
            {
                var version = Build.VERSION.SdkInt;

                if (version >= BuildVersionCodes.Gingerbread)
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "Checking Number of cameras...");

                    var numCameras = Camera.NumberOfCameras;
                    var camInfo = new Camera.CameraInfo();
                    var found = false;
                    Log.Debug(MobileBarcodeScanner.TAG, "Found " + numCameras + " cameras...");

                    var whichCamera = CameraFacing.Back;

                    if (_scannerHost.ScanningOptions.UseFrontCameraIfAvailable.HasValue &&
                        _scannerHost.ScanningOptions.UseFrontCameraIfAvailable.Value)
                        whichCamera = CameraFacing.Front;

                    for (var i = 0; i < numCameras; i++)
                    {
                        Camera.GetCameraInfo(i, camInfo);
                        if (camInfo.Facing == whichCamera)
                        {
                            Log.Debug(MobileBarcodeScanner.TAG,
                                "Found " + whichCamera + " Camera, opening...");
                            Camera = Camera.Open(i);
                            _cameraId = i;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        Log.Debug(MobileBarcodeScanner.TAG,
                            "Finding " + whichCamera + " camera failed, opening camera 0...");
                        Camera = Camera.Open(0);
                        _cameraId = 0;
                    }
                }
                else
                {
                    Camera = Camera.Open();
                }

            }
            catch (Exception ex)
            {
                ShutdownCamera();
                MobileBarcodeScanner.LogError("Setup Error: {0}", ex);
            }
        }

        private void ApplyCameraSettings()
        {
            if (Camera == null)
            {
                OpenCamera();
            }

            // do nothing if something wrong with camera
            if (Camera == null) return;

            var parameters = Camera.GetParameters();
            parameters.PreviewFormat = ImageFormatType.Nv21;

            var supportedFocusModes = parameters.SupportedFocusModes;

            if (_scannerHost.ScanningOptions.DisableAutofocus)
                parameters.FocusMode = Camera.Parameters.FocusModeFixed;
            else if (Build.VERSION.SdkInt >= BuildVersionCodes.IceCreamSandwich &&
                     supportedFocusModes.Contains(Camera.Parameters.FocusModeContinuousPicture))
            {
                _continuousAutofocus = true;
                parameters.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
            }
                
            else if (supportedFocusModes.Contains(Camera.Parameters.FocusModeContinuousVideo))
            {
                _continuousAutofocus = true;
                parameters.FocusMode = Camera.Parameters.FocusModeContinuousVideo;
            }
            else if (supportedFocusModes.Contains(Camera.Parameters.FocusModeAuto))
                parameters.FocusMode = Camera.Parameters.FocusModeAuto;
            else if (supportedFocusModes.Contains(Camera.Parameters.FocusModeFixed))
                parameters.FocusMode = Camera.Parameters.FocusModeFixed;

            Log.Debug(MobileBarcodeScanner.TAG, $"FocusMode ={parameters.FocusMode}");

            /*
             * - **** Imp ==> In UI project a layout should be created to mask other areas except the center rectangular area. 
             *                  To inform the user that app/ camera only scans the center rectangular area of the device.
             */
            SetBestPreviewFps(parameters);
            SetDefaultFocusArea(parameters);
            SetMetering(parameters);
            SetVideoStabilization(parameters);

            //SetRecordingHint to true also a workaround for low framerate on Nexus 4
            //https://stackoverflow.com/questions/14131900/extreme-camera-lag-on-nexus-4
            parameters.SetRecordingHint(true);

            if (_scannerHost.ScanningOptions.PureBarcode == true)
                SetBarcodeSceneMode(parameters);

            CameraResolution resolution = null;
            var supportedPreviewSizes = parameters.SupportedPreviewSizes;
            if (supportedPreviewSizes != null)
            {
                var availableResolutions = supportedPreviewSizes.Select(sps => new CameraResolution
                {
                    Width = sps.Width,
                    Height = sps.Height
                });

                // Try and get a desired resolution from the options selector
                resolution = _scannerHost.ScanningOptions.GetResolution(availableResolutions.ToList());

                // If the user did not specify a resolution, let's try and find a suitable one
                if (resolution == null)
                {
                    foreach (var sps in supportedPreviewSizes)
                    {
                        if (sps.Width >= 640 && sps.Width <= 1000 && sps.Height >= 360 && sps.Height <= 1000)
                        {
                            resolution = new CameraResolution
                            {
                                Width = sps.Width,
                                Height = sps.Height
                            };
                            break;
                        }
                    }
                }
            }

            // Google Glass requires this fix to display the camera output correctly
            if (Build.Model.Contains("Glass"))
            {
                resolution = new CameraResolution
                {
                    Width = 640,
                    Height = 360
                };
                // Glass requires 30fps
                parameters.SetPreviewFpsRange(30000, 30000);
            }

            // Hopefully a resolution was selected at some point
            if (resolution != null)
            {
                Log.Debug(MobileBarcodeScanner.TAG,
                    "Selected Resolution: " + resolution.Width + "x" + resolution.Height);
                parameters.SetPreviewSize(resolution.Width, resolution.Height);
            }

            Camera.SetParameters(parameters);

            SetCameraDisplayOrientation();

        }

        private void SetBestPreviewFps(Camera.Parameters parameters)
        {
            var selectedFps = parameters.SupportedPreviewFpsRange.FirstOrDefault();
            if (selectedFps != null)
            {
                Log.Debug(MobileBarcodeScanner.TAG,$"Old Selected fps Min:{selectedFps[0]}, Max {selectedFps[1]}");

                foreach (var fpsRange in parameters.SupportedPreviewFpsRange)
                {
                    if (fpsRange[1] >= selectedFps[1] && fpsRange[0] < selectedFps[0])
                        selectedFps = fpsRange;
                }

                Log.Debug(MobileBarcodeScanner.TAG,$" Setting Selected fps to Min:{selectedFps[0]}, Max {selectedFps[1]}");
                parameters.SetPreviewFpsRange(selectedFps[0], selectedFps[1]);
            }
        }

        private void SetDefaultFocusArea(Camera.Parameters parameters)
        {
            if (parameters?.MaxNumFocusAreas > 0)
            {
                List<Camera.Area> middleArea = BuildMiddleArea(AREA_PER_1000);
                Log.Debug(MobileBarcodeScanner.TAG, "Setting focus area to : " + middleArea.Select(f => f.Rect.FlattenToString()).Aggregate((first, next) => first + "; " + next));
                parameters.FocusAreas = middleArea;
            }
            else
            {
                Log.Debug(MobileBarcodeScanner.TAG, "Device does not support focus areas");
            }
        }

        private void SetMetering(Camera.Parameters parameters)
        {
            if (parameters?.MaxNumMeteringAreas > 0)
            {
                List<Camera.Area> middleArea = BuildMiddleArea(AREA_PER_1000);
                Log.Debug(MobileBarcodeScanner.TAG, "Setting metering areas: " + middleArea.Select(f => f.Rect.FlattenToString()).Aggregate((first, next) => first + "; " + next));
                parameters.MeteringAreas = middleArea;
            }
            else
            {
                Log.Debug(MobileBarcodeScanner.TAG, "Device does not support metering areas");
            }
        }

        private List<Camera.Area> BuildMiddleArea(int areaPer1000)
        {
            return new List<Camera.Area>()
                {
                    new Camera.Area(new Rect(-areaPer1000, -areaPer1000, areaPer1000, areaPer1000), 1)
                };
        }

        private void SetBarcodeSceneMode(Camera.Parameters parameters)
        {
            if (parameters.SceneMode == Camera.Parameters.SceneModeBarcode)
            {
                Log.Debug(MobileBarcodeScanner.TAG, "Barcode scene mode already set");
                return;
            }
            var supportedSceneModes = parameters.SupportedSceneModes;
            if (supportedSceneModes?.Contains(Camera.Parameters.SceneModeBarcode) == true)
            {
                Log.Debug(MobileBarcodeScanner.TAG, $"Previous SceneMode={parameters.SceneMode}");
                parameters.SceneMode = Camera.Parameters.SceneModeBarcode;
                Log.Debug(MobileBarcodeScanner.TAG, "Barcode scene mode is set");
            }
        }

        private void SetVideoStabilization(Camera.Parameters parameters)
        {
            if (parameters.IsVideoStabilizationSupported)
            {
                if (parameters.VideoStabilization)
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "Video stabilization already enabled");
                }
                else
                {
                    Log.Debug(MobileBarcodeScanner.TAG, "Enabling video stabilization...");
                    parameters.VideoStabilization = true;
                }
            }
            else
            {
                Log.Debug(MobileBarcodeScanner.TAG, "This device does not support video stabilization");
            }
        }

        private void AutoFocus(int x, int y, bool useCoordinates)
        {
            if (Camera == null) return;

            if (_scannerHost.ScanningOptions.DisableAutofocus)
            {
                Log.Debug(MobileBarcodeScanner.TAG, "AutoFocus Disabled");
                return;
            }

            var cameraParams = Camera.GetParameters();

            Log.Debug(MobileBarcodeScanner.TAG, $"AutoFocus Requested - x={x}, y={y},coord={useCoordinates}");

            // Cancel any previous requests
            Camera.CancelAutoFocus();

            try
            {
                // If we want to use coordinates
                // Also only if our camera supports Auto focus mode
                // Since FocusAreas only really work with FocusModeAuto set
                if (useCoordinates
                    && cameraParams.SupportedFocusModes.Contains(Camera.Parameters.FocusModeAuto))
                {
                    // Let's give the touched area a 20 x 20 minimum size rect to focus on
                    // So we'll offset -10 from the center of the touch and then 
                    // make a rect of 20 to give an area to focus on based on the center of the touch
                    x = x - 10;
                    y = y - 10;

                    // Ensure we don't go over the -1000 to 1000 limit of focus area
                    if (x >= 1000)
                        x = 980;
                    if (x < -1000)
                        x = -1000;
                    if (y >= 1000)
                        y = 980;
                    if (y < -1000)
                        y = -1000;

                    // Add our focus area
                    cameraParams.FocusAreas = new List<Camera.Area>
                    {
                        new Camera.Area(new Rect(x, y, x + 20, y + 20), 1000)
                    };

                    Log.Debug(MobileBarcodeScanner.TAG, $"AutoFocus area - x={x}, y={y}");

                    // Explicitly set FocusModeAuto since Focus areas only work with this setting
                    cameraParams.FocusMode = Camera.Parameters.FocusModeAuto;
                    Camera.SetParameters(cameraParams);

                    //with FocusModeAuto we are loosing the continuous autofocus
                    //lets reinstantiate it after 3 seconds
                    ReactivateContinuousAutofocus();
                }

                // Finally autofocus (weather we used focus areas or not)
                Camera.AutoFocus(_cameraEventListener);
            }
            catch (Exception ex)
            {
                Log.Debug(MobileBarcodeScanner.TAG, "AutoFocus Failed: {0}", ex);
                AutoFocus(x, y, useCoordinates);
            }
        }

        private void ReactivateContinuousAutofocus()
        {
            if (_continuousAutofocus)
            {
                cts?.Cancel();
                cts?.Dispose();
                cts = new CancellationTokenSource();
                
                Task.Delay(2000, cts.Token).ContinueWith( x =>
                {
                    if (!x.IsCanceled  && !x.IsFaulted)
                    {
                        var parameters = Camera.GetParameters();
                        parameters.PreviewFormat = ImageFormatType.Nv21;

                        var supportedFocusModes = parameters.SupportedFocusModes;

                        SetDefaultFocusArea(parameters);

                        if (Build.VERSION.SdkInt >= BuildVersionCodes.IceCreamSandwich &&
                            supportedFocusModes.Contains(Camera.Parameters.FocusModeContinuousPicture))
                        {
                            _continuousAutofocus = true;
                            parameters.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
                        }

                        else if (supportedFocusModes.Contains(Camera.Parameters.FocusModeContinuousVideo))
                        {
                            _continuousAutofocus = true;
                            parameters.FocusMode = Camera.Parameters.FocusModeContinuousVideo;
                        }

                        Camera.SetParameters(parameters);

                        Log.Debug(MobileBarcodeScanner.TAG, "AutoFocus reactivated!");
                    }
                    else
                    {
                        Log.Debug(MobileBarcodeScanner.TAG, "AutoFocus reactivation canceled");
                    }
                });
            }
        }

        private void SetCameraDisplayOrientation()
        {
            var degrees = GetCameraDisplayOrientation();
            LastCameraDisplayOrientationDegree = degrees;

            Log.Debug(MobileBarcodeScanner.TAG, "Changing Camera Orientation to: " + degrees);

            try
            {
                Camera.SetDisplayOrientation(degrees);
            }
            catch (Exception ex)
            {
                Log.Error(MobileBarcodeScanner.TAG, ex.ToString());
            }
        }

        private int GetCameraDisplayOrientation()
        {
            int degrees;
            var windowManager = _context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
            var display = windowManager.DefaultDisplay;
            var rotation = display.Rotation;

            LastDisplayOrientation = rotation;

            switch (rotation)
            {
                case SurfaceOrientation.Rotation0:
                    degrees = 0;
                    break;
                case SurfaceOrientation.Rotation90:
                    degrees = 90;
                    break;
                case SurfaceOrientation.Rotation180:
                    degrees = 180;
                    break;
                case SurfaceOrientation.Rotation270:
                    degrees = 270;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var info = new Camera.CameraInfo();
            Camera.GetCameraInfo(_cameraId, info);

            int correctedDegrees;
            if (info.Facing == CameraFacing.Front)
            {
                correctedDegrees = (info.Orientation + degrees) % 360;
                correctedDegrees = (360 - correctedDegrees) % 360; // compensate the mirror
            }
            else
            {
                // back-facing
                correctedDegrees = (info.Orientation - degrees + 360) % 360;
            }

            return correctedDegrees;
        }
    }
}
