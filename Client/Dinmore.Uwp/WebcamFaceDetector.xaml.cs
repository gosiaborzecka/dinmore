using dinmore.api.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace Dinmore.Uwp
{
    /// <summary>
    /// Page for demonstrating FaceTracking.
    /// </summary>
    public sealed partial class WebcamFaceDetector : Page
    {
        /// <summary>
        /// Brush for drawing the bounding box around each identified face.
        /// </summary>
        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);

        /// <summary>
        /// Thickness of the face bounding box lines.
        /// </summary>
        private readonly double lineThickness = 2.0;

        /// <summary>
        /// Transparent fill for the bounding box.
        /// </summary>
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);

        /// <summary>
        /// Holds the current scenario state value.
        /// </summary>
        private ScenarioState currentState;

        /// <summary>
        /// References a MediaCapture instance; is null when not in Streaming state.
        /// </summary>
        private MediaCapture mediaCapture;

        /// <summary>
        /// Cache of properties from the current MediaCapture device which is used for capturing the preview frame.
        /// </summary>
        private VideoEncodingProperties videoProperties;

        /// <summary>
        /// References a FaceTracker instance.
        /// </summary>
        private FaceTracker faceTracker;

        /// <summary>
        /// A periodic timer to execute FaceTracker on preview frames
        /// </summary>
        private ThreadPoolTimer frameProcessingTimer;

        /// <summary>
        /// Semaphore to ensure FaceTracking logic only executes one at a time
        /// </summary>
        private SemaphoreSlim frameProcessingSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Tracks the last time we asked the API anything so we don't get too chatty.
        /// </summary>
        private DateTimeOffset lastImageApiPush = DateTimeOffset.UtcNow;

        /// <summary>
        /// The minimum interval required between API calls.
        /// </summary>
        private const double ApiIntervalMs = 500;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebcamFaceDetector"/> class.
        /// </summary>
        public WebcamFaceDetector()
        {
            InitializeComponent();

            currentState = ScenarioState.Idle;
            App.Current.Suspending += OnSuspending;
        }

        /// <summary>
        /// Values for identifying and controlling scenario states.
        /// </summary>
        private enum ScenarioState
        {
            /// <summary>
            /// Camera is off and app is either starting up or shutting down.
            /// </summary>
            Idle,

            /// <summary>
            /// Webcam is running and looking for faces.
            /// </summary>
            WaitingForFaces,

            /// <summary>
            /// At least one face has been detected by the IoT device.
            /// </summary>
            FaceDetectedOnDevice,

            /// <summary>
            /// Call to API has been made and now waiting for response or timeout.
            /// </summary>
            WaitingForApiResponse,
        }

        /// <summary>
        /// Responds when we navigate to this page.
        /// </summary>
        /// <param name="e">Event data</param>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            // The 'await' operation can only be used from within an async method but class constructors
            // cannot be labeled as async, and so we'll initialize FaceTracker here.
            if (faceTracker == null)
            {
                faceTracker = await FaceTracker.CreateAsync();
                ChangeScenarioState(ScenarioState.WaitingForFaces);
            }
        }

        /// <summary>
        /// Responds to App Suspend event to stop/release MediaCapture object if it's running and return to Idle state.
        /// </summary>
        /// <param name="sender">The source of the Suspending event</param>
        /// <param name="e">Event data</param>
        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            if (currentState == ScenarioState.WaitingForFaces)
            {
                var deferral = e.SuspendingOperation.GetDeferral();
                try
                {
                    ChangeScenarioState(ScenarioState.Idle);
                }
                finally
                {
                    deferral.Complete();
                }
            }
        }

        /// <summary>
        /// Initializes a new MediaCapture instance and starts the Preview streaming to the CamPreview UI element.
        /// </summary>
        /// <returns>Async Task object returning true if initialization and streaming were successful and false if an exception occurred.</returns>
        private async Task<bool> StartWebcamStreaming()
        {
            bool successful = true;

            try
            {
                mediaCapture = new MediaCapture();

                // For this scenario, we only need Video (not microphone) so specify this in the initializer.
                // NOTE: the appxmanifest only declares "webcam" under capabilities and if this is changed to include
                // microphone (default constructor) you must add "microphone" to the manifest or initialization will fail.
                MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Video;
                await mediaCapture.InitializeAsync(settings);
                mediaCapture.Failed += MediaCapture_CameraStreamFailed;

                // Cache the media properties as we'll need them later.
                var deviceController = mediaCapture.VideoDeviceController;
                videoProperties = deviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

                // Immediately start streaming to our CaptureElement UI.
                // NOTE: CaptureElement's Source must be set before streaming is started.
                CamPreview.Source = mediaCapture;
                await mediaCapture.StartPreviewAsync();

                // Use a 66 millisecond interval for our timer, i.e. 15 frames per second
                TimeSpan timerInterval = TimeSpan.FromMilliseconds(66);
                frameProcessingTimer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(ProcessCurrentStateAsync), timerInterval);
            }
            catch (UnauthorizedAccessException)
            {
                // If the user has disabled their webcam this exception is thrown; provide a descriptive message to inform the user of this fact.
                //this.rootPage.NotifyUser("Webcam is disabled or access to the webcam is disabled for this app.\nEnsure Privacy Settings allow webcam usage.", NotifyType.ErrorMessage);
                successful = false;
            }
            catch (Exception ex)
            {
                //this.rootPage.NotifyUser(ex.ToString(), NotifyType.ErrorMessage);
                successful = false;
            }

            return successful;
        }

        /// <summary>
        /// Safely stops webcam streaming (if running) and releases MediaCapture object.
        /// </summary>
        private async void ShutdownWebCam()
        {
            if (frameProcessingTimer != null)
            {
                frameProcessingTimer.Cancel();
            }

            if (mediaCapture != null)
            {
                if (mediaCapture.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming)
                {
                    try
                    {
                        await mediaCapture.StopPreviewAsync();
                    }
                    catch (Exception)
                    {
                        ;   // Since we're going to destroy the MediaCapture object there's nothing to do here
                    }
                }
                mediaCapture.Dispose();
            }

            frameProcessingTimer = null;
            CamPreview.Source = null;
            mediaCapture = null;
        }

        byte[] lastFrame = null; // TODO: Global, needs reworking but difficult with state machine below.

        private async void ProcessCurrentStateAsync(ThreadPoolTimer timer)
        {
            switch (currentState)
            {
                case ScenarioState.Idle:
                    break;
                case ScenarioState.WaitingForFaces:
                    lastFrame = await ProcessCurrentVideoFrameAsync();

                    if (lastFrame != null)
                    {
                        ChangeScenarioState(ScenarioState.FaceDetectedOnDevice);
                    }
                    break;
                case ScenarioState.FaceDetectedOnDevice:
                    if (lastImageApiPush.AddMilliseconds(ApiIntervalMs) < DateTimeOffset.UtcNow)
                    {
                        ChangeScenarioState(ScenarioState.WaitingForApiResponse); // Prevent re-entry from other timers while waiting for response.
                        lastImageApiPush = DateTimeOffset.UtcNow;

                        var apiResult = await PostImageToApiAsync(lastFrame);

                        if (apiResult != null && apiResult.Any())
                        {
                            //// Create our visualization using the frame dimensions and face results but run it on the UI thread.
                            //var previewFrameSize = new Windows.Foundation.Size(previewFrame.SoftwareBitmap.PixelWidth, previewFrame.SoftwareBitmap.PixelHeight);
                            //var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            //{
                            //    SetupVisualization(previewFrameSize, faces);
                            //});
                        }
                        ChangeScenarioState(ScenarioState.WaitingForFaces);
                    }
                    break;
                case ScenarioState.WaitingForApiResponse:
                    // TODO: Check here for timeout or rely on timeout of HttpClient?
                    break;
                default:
                    ChangeScenarioState(ScenarioState.Idle);
                    break;
            }
        }

        private async Task<List<FaceWithEmotion>> PostImageToApiAsync(byte[] image)
        {
            using (var httpClient = new HttpClient())
            {
                var content = new StreamContent(new MemoryStream(image));
                content.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/octet-stream");

                var responseMessage = await httpClient.PostAsync("http://localhost:54169/api/patrons", content);

                var response = await responseMessage.Content.ReadAsStringAsync();

                // TODO: Process the json response here, also handle errors.
                //var xx = new JsonObject();
                //var p = JsonObject.Parse(response);
                //p.de
                //var x = new Windows.Data.Json.JsonArray();
                //x.pa
                //var emotionResponseArray = JArray.Parse(emotionResponseString);
                //var faces = new List<FaceWithEmotion>();
                //foreach (var emotionFaceResponse in emotionResponseArray)
                //{
                //    //deserialise json to face
                //    var face = JsonConvert.DeserializeObject<FaceWithEmotion>(emotionFaceResponse.ToString());

                //    //add face to faces list
                //    faces.Add(face);
                //}

                //return faces;


                return null;
            }
        }

        /// <summary>
        /// This method is invoked by a ThreadPoolTimer to execute the FaceTracker and Visualization logic at approximately 15 frames per second.
        /// </summary>
        /// <remarks>
        /// Keep in mind this method is called from a Timer and not synchronized with the camera stream. Also, the processing time of FaceTracker
        /// will vary depending on the size of each frame and the number of faces being tracked. That is, a large image with several tracked faces may
        /// take longer to process.
        /// </remarks>
        private async Task<byte[]> ProcessCurrentVideoFrameAsync()
        {
            // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
            // In this situation, don't wait on the semaphore but exit immediately.
            if (!frameProcessingSemaphore.Wait(0))
            {
                return null;
            }

            try
            {
                // Create a VideoFrame object specifying the pixel format we want our capture image to be (NV12 bitmap in this case).
                // GetPreviewFrame will convert the native webcam frame into this format.
                // TODO: Might be possible to side step the convert below by using RGBa16.
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                using (var previewFrame = new VideoFrame(InputPixelFormat, (int)videoProperties.Width, (int)videoProperties.Height))
                {
                    await mediaCapture.GetPreviewFrameAsync(previewFrame);

                    // The returned VideoFrame should be in the supported NV12 format but we need to verify this.
                    if (!FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                    {
                        throw new NotSupportedException("PixelFormat '" + InputPixelFormat.ToString() + "' is not supported by FaceDetector");
                    }
                    var faces = await faceTracker.ProcessNextFrameAsync(previewFrame);
                    if (faces.Any())
                    {
                        using (var ms = new MemoryStream())
                        {
                            // Create an encoder with the desired format
                            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms.AsRandomAccessStream());

                            // To use the encoder to resize we need to change the bitmap format. Might be a better way to do this.
                            var converted = SoftwareBitmap.Convert(previewFrame.SoftwareBitmap, BitmapPixelFormat.Rgba16);

                            // Set the software bitmap
                            encoder.SetSoftwareBitmap(converted);

                            var bounds = new BitmapBounds
                            {
                                X = faces.Min(x => x.FaceBox.X),
                                Y = faces.Min(y => y.FaceBox.Y),
                            };
                            bounds.Height = faces.Max(y => y.FaceBox.Y + y.FaceBox.Height) - bounds.Y;
                            bounds.Width = faces.Max(x => x.FaceBox.X + x.FaceBox.Width) - bounds.X;

                            encoder.BitmapTransform.Bounds = bounds;

                            await encoder.FlushAsync();
                            return ms.ToArray();
                        }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    //this.rootPage.NotifyUser(ex.ToString(), NotifyType.ErrorMessage);
                });
                return null;
            }
            finally
            {
                frameProcessingSemaphore.Release();
            }

        }

        /// <summary>
        /// Takes the webcam image and FaceTracker results and assembles the visualization onto the Canvas.
        /// </summary>
        /// <param name="framePizelSize">Width and height (in pixels) of the video capture frame</param>
        /// <param name="foundFaces">List of detected faces; output from FaceTracker</param>
        private void SetupVisualization(Windows.Foundation.Size framePizelSize, IList<DetectedFace> foundFaces)
        {
            VisualizationCanvas.Children.Clear();

            double actualWidth = VisualizationCanvas.ActualWidth;
            double actualHeight = VisualizationCanvas.ActualHeight;

            if (currentState == ScenarioState.WaitingForFaces && foundFaces != null && actualWidth != 0 && actualHeight != 0)
            {
                double widthScale = framePizelSize.Width / actualWidth;
                double heightScale = framePizelSize.Height / actualHeight;

                foreach (DetectedFace face in foundFaces)
                {
                    // Create a rectangle element for displaying the face box but since we're using a Canvas
                    // we must scale the rectangles according to the frames's actual size.
                    Rectangle box = new Rectangle();
                    box.Width = (uint)(face.FaceBox.Width / widthScale);
                    box.Height = (uint)(face.FaceBox.Height / heightScale);
                    box.Fill = fillBrush;
                    box.Stroke = lineBrush;
                    box.StrokeThickness = lineThickness;
                    box.Margin = new Thickness((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), 0, 0);

                    VisualizationCanvas.Children.Add(box);
                }
            }
        }

        /// <summary>
        /// Manages the scenario's internal state. Invokes the internal methods and updates the UI according to the
        /// passed in state value. Handles failures and resets the state if necessary.
        /// </summary>
        /// <param name="newState">State to switch to</param>
        private async void ChangeScenarioState(ScenarioState newState)
        {
            switch (newState)
            {
                case ScenarioState.Idle:

                    ShutdownWebCam();

                    VisualizationCanvas.Children.Clear();
                    break;

                case ScenarioState.WaitingForFaces:

                    if (!await StartWebcamStreaming())
                    {
                        ChangeScenarioState(ScenarioState.Idle);
                        break;
                    }

                    VisualizationCanvas.Children.Clear();
                    break;
            }
            currentState = newState;
        }

        /// <summary>
        /// Handles MediaCapture stream failures by shutting down streaming and returning to Idle state.
        /// </summary>
        /// <param name="sender">The source of the event, i.e. our MediaCapture object</param>
        /// <param name="args">Event data</param>
        private void MediaCapture_CameraStreamFailed(MediaCapture sender, object args)
        {
            // MediaCapture is not Agile and so we cannot invoke its methods on this caller's thread
            // and instead need to schedule the state change on the UI thread.
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ChangeScenarioState(ScenarioState.Idle);
            });
        }
    }
}
