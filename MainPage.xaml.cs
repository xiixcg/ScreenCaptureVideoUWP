using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using SharpDX.Direct3D11;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace ScreenCaptureTest {
	/// <summary>
	/// An empty page that can be used on its own or navigated to within a Frame.
	/// </summary>
	public sealed partial class MainPage : Page {
		// Capture API objects.
		private SizeInt32 _lastSize;
		private GraphicsCaptureItem _item;
		private Direct3D11CaptureFramePool _framePool;
		private GraphicsCaptureSession _session;

		// Non-API related members.
		private CanvasDevice _canvasDevice;
		private CompositionGraphicsDevice _compositionGraphicsDevice;
		private Compositor _compositor;
		private CompositionDrawingSurface _surface;

		private CanvasBitmap _lastScreenshotFrame;
		private IDirect3DDevice _device;
		private Device _sharpDxD3dDevice;
		private GraphicsCaptureItem _captureItem;
		private Texture2D _composeTexture;
		private RenderTargetView _composeRenderTargetView;
		private MediaEncodingProfile _encodingProfile;
		private VideoStreamDescriptor _videoDescriptor;
		private MediaStreamSource _mediaStreamSource;
		private MediaTranscoder _transcoder;
		private bool _isRecording;
		private bool _closed;
		private Multithread _multithread;
		private ManualResetEvent _frameEvent;
		private ManualResetEvent _closedEvent;
		private ManualResetEvent[] _events;
		private Direct3D11CaptureFrame _currentFrame;

		public MainPage() {
			this.InitializeComponent();
			Task setup = SetupEncoding();
		}

		private async Task SetupEncoding() {
			if(!GraphicsCaptureSession.IsSupported()) {
				// Show message to user that screen capture is unsupported
				return;
			}

			// Create the D3D device and SharpDX device
			if(_device == null) {
				_device = Direct3D11Helpers.CreateD3DDevice();
			}
			if(_sharpDxD3dDevice == null) {
				_sharpDxD3dDevice = Direct3D11Helpers.CreateSharpDXDevice(_device);
			}



			try {
				// Let the user pick an item to capture
				var picker = new GraphicsCapturePicker();
				_captureItem = await picker.PickSingleItemAsync();
				if(_captureItem == null) {
					return;
				}

				// Initialize a blank texture and render target view for copying frames, using the same size as the capture item
				_composeTexture = Direct3D11Helpers.InitializeComposeTexture(_sharpDxD3dDevice, _captureItem.Size);
				_composeRenderTargetView = new SharpDX.Direct3D11.RenderTargetView(_sharpDxD3dDevice, _composeTexture);

				// This example encodes video using the item's actual size.
				var width = (uint) _captureItem.Size.Width;
				var height = (uint) _captureItem.Size.Height;

				// Make sure the dimensions are are even. Required by some encoders.
				width = (width % 2 == 0) ? width : width + 1;
				height = (height % 2 == 0) ? height : height + 1;


				var temp = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
				var bitrate = temp.Video.Bitrate;
				uint framerate = 30;

				_encodingProfile = new MediaEncodingProfile();
				_encodingProfile.Container.Subtype = "MPEG4";
				_encodingProfile.Video.Subtype = "H264";
				_encodingProfile.Video.Width = width;
				_encodingProfile.Video.Height = height;
				_encodingProfile.Video.Bitrate = bitrate;
				_encodingProfile.Video.FrameRate.Numerator = framerate;
				_encodingProfile.Video.FrameRate.Denominator = 1;
				_encodingProfile.Video.PixelAspectRatio.Numerator = 1;
				_encodingProfile.Video.PixelAspectRatio.Denominator = 1;

				var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, width, height);
				_videoDescriptor = new VideoStreamDescriptor(videoProperties);

				// Create our MediaStreamSource
				_mediaStreamSource = new MediaStreamSource(_videoDescriptor);
				_mediaStreamSource.BufferTime = TimeSpan.FromSeconds(0);
				_mediaStreamSource.Starting += OnMediaStreamSourceStarting;
				_mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;

				// Create our transcoder
				_transcoder = new MediaTranscoder();
				_transcoder.HardwareAccelerationEnabled = true;


				// Create a destination file - Access to the VideosLibrary requires the "Videos Library" capability
				var folder = KnownFolders.SavedPictures;
				//var folder = KnownFolders.VideosLibrary;
				var name = DateTime.Now.ToString("yyyyMMddHHmmss");
				var file = await folder.CreateFileAsync($"{name}.mp4");

				using(var stream = await file.OpenAsync(FileAccessMode.ReadWrite))

					await EncodeAsync(stream);

			}
			catch(Exception ex) {

				return;
			}
		}

		private async Task EncodeAsync(IRandomAccessStream stream) {
			if(!_isRecording) {
				_isRecording = true;

				StartCapture();

				var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, stream, _encodingProfile);

				await transcode.TranscodeAsync();
			}
		}

		private void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args) {
			if(_isRecording && !_closed) {
				try {
					using(var frame = WaitForNewFrame()) {
						if(frame == null) {
							args.Request.Sample = null;
							Stop();
							Cleanup();
							return;
						}

						var timeStamp = frame.SystemRelativeTime;

						var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
						args.Request.Sample = sample;
					}
				}
				catch(Exception e) {
					Debug.WriteLine(e.Message);
					Debug.WriteLine(e.StackTrace);
					Debug.WriteLine(e);
					args.Request.Sample = null;
					Stop();
					Cleanup();
				}
			}
			else {
				args.Request.Sample = null;
				Stop();
				Cleanup();
			}
		}

		private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) {
			using(var frame = WaitForNewFrame()) {
				args.Request.SetActualStartPosition(frame.SystemRelativeTime);
			}
		}

		public void StartCapture() {

			_multithread = _sharpDxD3dDevice.QueryInterface<SharpDX.Direct3D11.Multithread>();
			_multithread.SetMultithreadProtected(true);
			_frameEvent = new ManualResetEvent(false);
			_closedEvent = new ManualResetEvent(false);
			_events = new[] { _closedEvent, _frameEvent };

			_captureItem.Closed += OnClosed;
			_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
				_device,
				DirectXPixelFormat.B8G8R8A8UIntNormalized,
				1,
				_captureItem.Size);
			_framePool.FrameArrived += OnFrameArrived;
			_session = _framePool.CreateCaptureSession(_captureItem);
			_session.StartCapture();
		}

		private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args) {
			_currentFrame = sender.TryGetNextFrame();
			_frameEvent.Set();
		}

		private void OnClosed(GraphicsCaptureItem sender, object args) {
			_closedEvent.Set();
		}

		public SurfaceWithInfo WaitForNewFrame() {
			// Let's get a fresh one.
			_currentFrame?.Dispose();
			_frameEvent.Reset();

			var signaledEvent = _events[WaitHandle.WaitAny(_events)];
			if(signaledEvent == _closedEvent) {
				Cleanup();
				return null;
			}

			var result = new SurfaceWithInfo();
			result.SystemRelativeTime = _currentFrame.SystemRelativeTime;
			using(var multithreadLock = new MultithreadLock(_multithread))
			using(var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(_currentFrame.Surface)) {

				_sharpDxD3dDevice.ImmediateContext.ClearRenderTargetView(_composeRenderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));

				var width = Math.Clamp(_currentFrame.ContentSize.Width, 0, _currentFrame.Surface.Description.Width);
				var height = Math.Clamp(_currentFrame.ContentSize.Height, 0, _currentFrame.Surface.Description.Height);
				var region = new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, width, height, 1);
				_sharpDxD3dDevice.ImmediateContext.CopySubresourceRegion(sourceTexture, 0, region, _composeTexture, 0);

				var description = sourceTexture.Description;
				description.Usage = SharpDX.Direct3D11.ResourceUsage.Default;
				description.BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget;
				description.CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None;
				description.OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None;

				using(var copyTexture = new SharpDX.Direct3D11.Texture2D(_sharpDxD3dDevice, description)) {
					_sharpDxD3dDevice.ImmediateContext.CopyResource(_composeTexture, copyTexture);
					result.Surface = Direct3D11Helpers.CreateDirect3DSurfaceFromSharpDXTexture(copyTexture);
				}
			}

			return result;
		}

		private void Stop() {
			_closedEvent.Set();
		}

		private void Cleanup() {
			_framePool?.Dispose();
			_session?.Dispose();
			if(_captureItem != null) {
				_captureItem.Closed -= OnClosed;
			}
			_captureItem = null;
			_device = null;
			_sharpDxD3dDevice = null;
			_composeTexture?.Dispose();
			_composeTexture = null;
			_composeRenderTargetView?.Dispose();
			_composeRenderTargetView = null;
			_currentFrame?.Dispose();
		}


		//public async Task StartCaptureAsync() {
		//	// The GraphicsCapturePicker follows the same pattern the
		//	// file pickers do.
		//	var picker = new GraphicsCapturePicker();
		//	GraphicsCaptureItem item = await picker.PickSingleItemAsync();

		//	// The item may be null if the user dismissed the
		//	// control without making a selection or hit Cancel.
		//	if(item != null) {
		//		StartCaptureInternal(item);
		//	}
		//}

		//private void StartCaptureInternal(GraphicsCaptureItem item) {
		//	// Stop the previous capture if we had one.
		//	StopCapture();

		//	_item = item;
		//	_lastSize = _item.Size;

		//	_framePool = Direct3D11CaptureFramePool.Create(
		//	   _canvasDevice, // D3D device
		//	   DirectXPixelFormat.B8G8R8A8UIntNormalized, // Pixel format
		//	   2, // Number of frames
		//	   _item.Size); // Size of the buffers

		//	_framePool.FrameArrived += (s, a) => {
		//		// The FrameArrived event is raised for every frame on the thread
		//		// that created the Direct3D11CaptureFramePool. This means we
		//		// don't have to do a null-check here, as we know we're the only
		//		// one dequeueing frames in our application.  

		//		// NOTE: Disposing the frame retires it and returns  
		//		// the buffer to the pool.

		//		using(var frame = _framePool.TryGetNextFrame()) {
		//			ProcessFrame(frame);
		//		}
		//	};

		//	_item.Closed += (s, a) => {
		//		StopCapture();
		//	};

		//	_session = _framePool.CreateCaptureSession(_item);
		//	_session.StartCapture();
		//}

		//public void StopCapture() {
		//	_session?.Dispose();
		//	_framePool?.Dispose();
		//	_item = null;
		//	_session = null;
		//	_framePool = null;
		//}

		//private void ProcessFrame(Direct3D11CaptureFrame frame) {
		//	// Resize and device-lost leverage the same function on the
		//	// Direct3D11CaptureFramePool. Refactoring it this way avoids
		//	// throwing in the catch block below (device creation could always
		//	// fail) along with ensuring that resize completes successfully and
		//	// isn’t vulnerable to device-lost.
		//	bool needsReset = false;
		//	bool recreateDevice = false;

		//	if((frame.ContentSize.Width != _lastSize.Width) ||
		//		(frame.ContentSize.Height != _lastSize.Height)) {
		//		needsReset = true;
		//		_lastSize = frame.ContentSize;
		//	}

		//	try {
		//		// Take the D3D11 surface and draw it into a  
		//		// Composition surface.

		//		// Convert our D3D11 surface into a Win2D object.
		//		CanvasBitmap canvasBitmap = CanvasBitmap.CreateFromDirect3D11Surface(
		//			_canvasDevice,
		//			frame.Surface);

		//		_currentFrame = canvasBitmap;

		//		// Helper that handles the drawing for us.
		//	}

		//	// This is the device-lost convention for Win2D.
		//	catch(Exception e) when(_canvasDevice.IsDeviceLost(e.HResult)) {
		//		// We lost our graphics device. Recreate it and reset
		//		// our Direct3D11CaptureFramePool.  
		//		needsReset = true;
		//		recreateDevice = true;
		//	}

		//	if(needsReset) {
		//		ResetFramePool(frame.ContentSize, recreateDevice);
		//	}
		//}

		//private void FillSurfaceWithBitmap(CanvasBitmap canvasBitmap) {
		//	Size smallerSize = new Size(canvasBitmap.Size.Width * 2, canvasBitmap.Size.Height * 2);
		//	CanvasComposition.Resize(_surface, smallerSize);

		//	using(var session = CanvasComposition.CreateDrawingSession(_surface)) {
		//		session.Clear(Colors.Transparent);
		//		session.DrawImage(canvasBitmap);
		//	}
		//}

		//private void ResetFramePool(SizeInt32 size, bool recreateDevice) {
		//	do {
		//		try {
		//			if(recreateDevice) {
		//				_canvasDevice = new CanvasDevice();
		//			}

		//			_framePool.Recreate(
		//				_canvasDevice,
		//				DirectXPixelFormat.B8G8R8A8UIntNormalized,
		//				2,
		//				size);
		//		}
		//		// This is the device-lost convention for Win2D.
		//		catch(Exception e) when(_canvasDevice.IsDeviceLost(e.HResult)) {
		//			_canvasDevice = null;
		//			recreateDevice = true;
		//		}
		//	} while(_canvasDevice == null);
		//}

		//private async void Button_ClickAsync(object sender, RoutedEventArgs e) {
		//	await StartCaptureAsync();
		//}

		//private async void ScreenshotButton_ClickAsync(object sender, RoutedEventArgs e) {
		//	await SaveImageAsync($"{DateTime.Now.ToString("MMDDyyyyHHmmssfff")}.png", _currentFrame);
		//	_lastScreenshotFrame = _currentFrame;
		//	FillSurfaceWithBitmap(_lastScreenshotFrame);
		//}

		//private async Task SaveImageAsync(string filename, CanvasBitmap frame) {
		//	StorageFolder pictureFolder = KnownFolders.SavedPictures;

		//	StorageFile file = await pictureFolder.CreateFileAsync(
		//		filename,
		//		CreationCollisionOption.ReplaceExisting);

		//	using(var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite)) {
		//		await frame.SaveAsync(fileStream, CanvasBitmapFileFormat.Png, 1f);
		//	}
		//}
	}
}