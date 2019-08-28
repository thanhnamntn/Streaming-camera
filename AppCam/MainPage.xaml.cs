using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.System.Profile;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace AppCam
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        Button _btnSwitchCamera;
        CheckBox _chkCycleCameras;
        Image _img = new Image();
        TextBlock _tbStatus = new TextBlock();
        object _timerLock = new object();

        int _cameratoUse = 0;
        DeviceInformationCollection _cameraDevices = null;
        List<MediaCapture> _lstMedCapture = new List<MediaCapture>();
        bool _fUseNetworkTime = false;
        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                //                this.Background = new SolidColorBrush(Color.FromArgb(255,128, 128, 128));
                if (AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.IoT")
                {
                    // "Windows.Desktop"
                    // "Windows.Mobile" (phone)
                    // "Windows.IoT"
                    _fUseNetworkTime = true;
                }
                Action resetCameras = () =>
                {
                    lock (_timerLock)
                    {
                        _cameraDevices = null;// force reload
                    }
                };
                var deviceWatcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
                deviceWatcher.Added += new TypedEventHandler<DeviceWatcher, DeviceInformation>(
                    (wat, info) => { resetCameras(); });
                deviceWatcher.Removed += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(
                    (wat, info) => { resetCameras(); });
                deviceWatcher.Updated += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(
                    (wat, info) => { resetCameras(); });
                deviceWatcher.Stopped += new TypedEventHandler<DeviceWatcher, object>(
                    (wat, obj) => { deviceWatcher.Start(); });
                deviceWatcher.Start();
                var relPanel = new RelativePanel();
                var spCtrls = new StackPanel()
                {
                    Orientation = Orientation.Horizontal
                };
                _img.HorizontalAlignment = HorizontalAlignment.Center;
                _img.Stretch = Stretch.UniformToFill;
                _btnSwitchCamera = new Button()
                {
                    IsEnabled = _cameraDevices?.Count > 1,
                    Width = 260
                };
                SetBtnSwitchLabel();
                ToolTipService.SetToolTip(_btnSwitchCamera, new ToolTip()
                {
                    Content = "Click to switch camera if available"
                });
                spCtrls.Children.Add(_btnSwitchCamera);
                _btnSwitchCamera.Click += (oc, ec) =>
                {
                    IncrementCameraInUse();
                    SetBtnSwitchLabel();
                };
                _chkCycleCameras = new CheckBox()
                {
                    Content = "Cycle Cameras",
                    IsChecked = false
                };
                ToolTipService.SetToolTip(_chkCycleCameras, new ToolTip()
                {
                    Content = "Automatically switch through all attached cameras"
                });
                spCtrls.Children.Add(_chkCycleCameras);
                relPanel.Children.Add(spCtrls);
                var tbInterval = new TextBox()
                {
                    Text = "7"
                };
                spCtrls.Children.Add(tbInterval);
                var btnQuit = new Button()
                {
                    Content = "Quit"
                };
                spCtrls.Children.Add(btnQuit);
                btnQuit.Click += (oq, eq) =>
                {
                    lock (_timerLock)
                    {
                        // make sure we're done with cam before exit
                        Application.Current.Exit();
                    }
                };
                spCtrls.Children.Add(_tbStatus);
                relPanel.Children.Add(_img);
                RelativePanel.SetBelow(_img, spCtrls);
                var tmr = new DispatcherTimer();
                tmr.Interval = TimeSpan.FromSeconds(4);
                tbInterval.LostFocus += (otb, etb) =>
                {
                    double n;
                    if (double.TryParse(tbInterval.Text, out n))
                    {
                        tmr.Interval = TimeSpan.FromSeconds(n);
                    }
                };
                bool fIsInTickRoutine = false;
                _tsSinceLastTimeCheck = TimeSpan.FromDays(1); // force time check
                tmr.Tick += async (ot, et) =>
                {
                    if (!fIsInTickRoutine)
                    {
                        fIsInTickRoutine = true;
                        if (Monitor.TryEnter(_timerLock))
                        {
                            try
                            {
                                if (_fUseNetworkTime)
                                {
                                    _tsSinceLastTimeCheck += tmr.Interval;
                                    if (_tsSinceLastTimeCheck.TotalMinutes >= 1)
                                    {
                                        // resync the clock
                                        try
                                        {
                                            _dtLastTimeCheck = await NtpClient.GetDateTimeAsync();
                                            _tsSinceLastTimeCheck = TimeSpan.Zero;
                                        }
                                        catch (Exception ex)
                                        {
                                            _tbStatus.Text = ex.ToString(); // task cancelled exception
                                        }
                                    }
                                }
                                await LookForCameraAndTakeAPicture();
                            }
                            finally
                            {
                                Monitor.Exit(_timerLock);
                            }
                        }
                        fIsInTickRoutine = false;
                    }
                };
                tmr.Start();
                this.Content = relPanel;
            }
            catch (Exception ex)
            {
                this.Content = new TextBlock() { Text = ex.ToString() };
            }
        }
        void IncrementCameraInUse()
        {
            lock (_timerLock)
            {
                if (++_cameratoUse == _cameraDevices.Count)
                {
                    _cameratoUse = 0;
                }
            }
        }

        TimeSpan _tsSinceLastTimeCheck;
        DateTime _dtLastTimeCheck;
        DateTime CurrentDateTime { get { return _dtLastTimeCheck + _tsSinceLastTimeCheck; } }
        async Task LookForCameraAndTakeAPicture()
        {
            try
            {
                bool fWasCycling = _chkCycleCameras.IsChecked == true;
                DateTime now;
                if (_fUseNetworkTime)
                {
                    now = CurrentDateTime;
                }
                else
                {
                    now = DateTime.Now;
                }
                _tbStatus.Text = now.ToString("MM/dd/yy hh:mm:ss tt") + " " + _tsSinceLastTimeCheck.TotalMinutes.ToString("n1");
                // do we need to initialize or reinitialize?
                if (_cameraDevices == null || _cameraDevices.Count == 0)
                {
                    _chkCycleCameras.IsChecked = false;
                    await initializeCamerasAsync();
                }
                else if (_chkCycleCameras.IsChecked == true)
                {
                    IncrementCameraInUse();
                }
                SetBtnSwitchLabel();
                var bmImage = await TakePictureAsync();
                _img.Source = bmImage;
                _img.HorizontalAlignment = HorizontalAlignment.Center;
                if (fWasCycling && _cameraDevices?.Count > 1)
                {
                    _chkCycleCameras.IsChecked = true;
                }
            }
            catch (Exception ex)
            {
                _tbStatus.Text += ex.ToString();
                _cameraDevices = null; // will reset looking for camera
                var comex = ex as COMException;
                if (comex != null)
                {
                    if (comex.Message.Contains("The video recording device is no longer present"))
                    {
                        // could be more specific
                    }
                }
            }
        }

        async Task initializeCamerasAsync()
        {
            _cameratoUse = 0;
            _lstMedCapture.Clear();
            _chkCycleCameras.IsChecked = false;
            _chkCycleCameras.IsEnabled = false;
            _cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            switch (_cameraDevices.Count)
            {
                case 0:
                    _btnSwitchCamera.Content = " No camera found";
                    _chkCycleCameras.IsChecked = false;
                    break;
                case 1:
                    _chkCycleCameras.IsChecked = false;
                    _btnSwitchCamera.IsEnabled = false;
                    break;
                default:
                    _btnSwitchCamera.IsEnabled = true;
                    break;
            }
            if (_cameraDevices.Count > 0)
            {
                _chkCycleCameras.IsEnabled = _cameraDevices.Count > 1;
                _chkCycleCameras.IsChecked = _cameraDevices.Count > 1;
                int ndx = 0;
                int nFrontCamera = -1;
                foreach (var cam in _cameraDevices)
                { // high priority for front camera
                    if (cam.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front)
                    {
                        nFrontCamera = ndx;
                    }
                    var medCapture = new MediaCapture();
                    MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
                    //settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
                    //settings.PhotoCaptureSource = PhotoCaptureSource.VideoPreview;
                    //                    var exposuretime = _medCapture.VideoDeviceController.ExposureControl.Value;
                    settings.VideoDeviceId = _cameraDevices[ndx].Id;
                    medCapture.Failed += (o, e) =>
                    {
                        //                        _tbStatus.Text += e.Message;
                    };
                    await medCapture.InitializeAsync(settings);
                    _lstMedCapture.Add(medCapture);
                    ndx++;
                }
                if (nFrontCamera >= 0)
                {
                    _cameratoUse = nFrontCamera;
                }
            }
        }

        void SetBtnSwitchLabel()
        {
            var camName = "No Camera";
            if (_cameraDevices != null)
            {
                var dev = _cameraDevices[_cameratoUse];

                var camLoc = dev.EnclosureLocation?.Panel.ToString();
                camName = $"{_cameratoUse} {dev.Name} {camLoc}".Trim();
            }
            _btnSwitchCamera.Content = camName;
        }

        async Task<BitmapImage> TakePictureAsync()
        {
            // https://docs.microsoft.com/en-us/windows/uwp/audio-video-camera/basic-photo-video-and-audio-capture-with-mediacapture
            var medCapture = _lstMedCapture[_cameratoUse];
            var imgFmt = ImageEncodingProperties.CreateJpeg();
            imgFmt.Height = 480;
            imgFmt.Width = 640;
            
            var llCapture = await medCapture.PrepareLowLagPhotoCaptureAsync(imgFmt);
            var photo = await llCapture.CaptureAsync();
            var bmImage = new BitmapImage();

            await bmImage.SetSourceAsync(photo.Frame);


            await llCapture.FinishAsync();
            return bmImage;

            //var camCapUI = new CameraCaptureUI();
            //camCapUI.PhotoSettings.AllowCropping = true;
            //camCapUI.PhotoSettings.Format = CameraCaptureUIPhotoFormat.Jpeg;
            //var storageFile = await camCapUI.CaptureFileAsync(CameraCaptureUIMode.Photo);
            //var bmImage = new BitmapImage();
            //if (storageFile != null)
            //{
            //    using (var strm = await storageFile.OpenReadAsync())
            //    {
            //        bmImage.SetSource(strm);
            //    }
            //}
        }
    }

    //http://stackoverflow.com/questions/1193955/how-to-query-an-ntp-server-using-c
    /// <summary>
    /// Represents a client which can obtain accurate time via NTP protocol.
    /// </summary>
    public class NtpClient
    {
        private readonly TaskCompletionSource<DateTime> _resultCompletionSource;
        public async static Task<DateTime> GetDateTimeAsync()
        {
            var ntpClient = new NtpClient();
            return await ntpClient.GetNetworkTimeAsync();
        }
        /// <summary>
        /// Creates a new instance of <see cref="NtpClient"/> class.
        /// </summary>
        public NtpClient()
        {
            _resultCompletionSource = new TaskCompletionSource<DateTime>();
        }

        /// <summary>
        /// Gets accurate time using the NTP protocol with default timeout of 45 seconds.
        /// </summary>
        /// <returns>Network accurate <see cref="DateTime"/> value.</returns>
        public async Task<DateTime> GetNetworkTimeAsync()
        {
            var utcNow = await GetNetworkTimeAsync(TimeSpan.FromSeconds(45));
            var tzOffset = System.TimeZoneInfo.Local.GetUtcOffset(utcNow); // -7 hrs for Redmond with DST
            var dtNow = utcNow + tzOffset;
            return dtNow;
        }

        /// <summary>
        /// Gets accurate time using the NTP protocol with default timeout of 45 seconds.
        /// </summary>
        /// <param name="timeout">Operation timeout.</param>
        /// <returns>Network accurate <see cref="DateTime"/> value.</returns>
        public async Task<DateTime> GetNetworkTimeAsync(TimeSpan timeout)
        {
            using (var socket = new DatagramSocket())
            using (var ct = new CancellationTokenSource(timeout))
            {
                ct.Token.Register(() => _resultCompletionSource.TrySetCanceled());

                socket.MessageReceived += OnSocketMessageReceived;
                //The UDP port number assigned to NTP is 123
                await socket.ConnectAsync(new HostName("pool.ntp.org"), "123");
                using (var writer = new DataWriter(socket.OutputStream))
                {
                    // NTP message size is 16 bytes of the digest (RFC 2030)
                    var ntpBuffer = new byte[48];

                    // Setting the Leap Indicator, 
                    // Version Number and Mode values
                    // LI = 0 (no warning)
                    // VN = 3 (IPv4 only)
                    // Mode = 3 (Client Mode)
                    ntpBuffer[0] = 0x1B;

                    writer.WriteBytes(ntpBuffer);
                    await writer.StoreAsync();
                    var result = await _resultCompletionSource.Task;
                    return result;
                }
            }
        }

        private void OnSocketMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var reader = args.GetDataReader())
                {
                    byte[] response = new byte[48];
                    reader.ReadBytes(response);
                    _resultCompletionSource.TrySetResult(ParseNetworkTime(response));
                }
            }
            catch (Exception ex)
            {
                _resultCompletionSource.TrySetException(ex);
            }
        }

        private static DateTime ParseNetworkTime(byte[] rawData)
        {
            //Offset to get to the "Transmit Timestamp" field (time at which the reply 
            //departed the server for the client, in 64-bit timestamp format."
            const byte serverReplyTime = 40;

            //Get the seconds part
            ulong intPart = BitConverter.ToUInt32(rawData, serverReplyTime);

            //Get the seconds fraction
            ulong fractPart = BitConverter.ToUInt32(rawData, serverReplyTime + 4);

            //Convert From big-endian to little-endian
            intPart = SwapEndianness(intPart);
            fractPart = SwapEndianness(fractPart);

            var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);

            //**UTC** time
            DateTime networkDateTime = (new DateTime(1900, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).AddMilliseconds((long)milliseconds);
            return networkDateTime;
        }

        // stackoverflow.com/a/3294698/162671
        private static uint SwapEndianness(ulong x)
        {
            return (uint)(((x & 0x000000ff) << 24) +
                           ((x & 0x0000ff00) << 8) +
                           ((x & 0x00ff0000) >> 8) +
                           ((x & 0xff000000) >> 24));
        }
    }

}

