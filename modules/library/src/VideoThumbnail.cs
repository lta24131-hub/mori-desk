using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Mori
{
	internal static class VideoThumbnail
	{
		private struct SIZE
		{
			public int X;

			public int Y;
		}

		[ComImport]
		[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellItemImageFactory
		{
			[PreserveSig]
			int GetImage(SIZE size, int flags, out IntPtr bitmap);
		}

		private struct PROPERTYKEY
		{
			public Guid fmtid;

			public uint pid;
		}

		[StructLayout(LayoutKind.Explicit, Size = 24)]
		private struct PROPVARIANT
		{
			[FieldOffset(0)]
			public ushort type;

			[FieldOffset(8)]
			public uint value;
		}

		[ComImport]
		[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IPropertyStore
		{
			[PreserveSig]
			int GetCount(out uint count);

			[PreserveSig]
			int GetAt(uint index, out PROPERTYKEY key);

			[PreserveSig]
			int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

			[PreserveSig]
			int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

			[PreserveSig]
			int Commit();
		}

		internal const int MaximumOutput = 3145728;

		internal const int MaximumDiagnostic = 65536;

		internal const int MaximumMilliseconds = 15000;

		internal const long MaximumPrivateMemory = 268435456L;

		private static readonly SemaphoreSlim Workers = new SemaphoreSlim(1);

		internal static string LastFailure;

		internal static string LastWorkerDiagnostic;

		internal static readonly ThumbnailFailureCache FailureCache = new ThumbnailFailureCache(TimeSpan.FromMinutes(10.0), 128);

		internal static void ClearFailures()
		{
			FailureCache.Clear();
		}

		internal static BitmapSource Render(string path, out int width, out int height)
		{
			width = (height = 0);
			LastFailure = null;
			LastWorkerDiagnostic = null;
			string stamp = ThumbnailBuildGate.SourceStamp(path);
			if (FailureCache.Has(stamp, DateTime.UtcNow))
			{
				return null;
			}
			Workers.Wait();
			long version = FailureCache.Version;
			try
			{
				if (FailureCache.Has(stamp, DateTime.UtcNow))
				{
					return null;
				}
				var startInfo = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--video-thumbnail \"" + Path.GetFullPath(path) + "\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
				using (var process = Process.Start(startInfo))
				{
					if (process == null)
					{
						throw new NotSupportedException("无法启动视频缩略图解码。");
					}
					Task<byte[]> task = Task.Run(delegate { return ReadBounded(process.StandardError.BaseStream, 65536); });
					Task<byte[]> task2 = Task.Run(delegate { return ReadBounded(process.StandardOutput.BaseStream, 3145728); });
					Stopwatch stopwatch = Stopwatch.StartNew();
					string text = null;
					try
					{
						while (!process.WaitForExit(50))
						{
							if (task2.IsFaulted || task.IsFaulted)
							{
								throw new InvalidDataException("视频缩略图输出超过安全范围。");
							}
							process.Refresh();
							if (stopwatch.ElapsedMilliseconds > 15000)
							{
								text = "timeout";
								break;
							}
							if (process.PrivateMemorySize64 > 268435456)
							{
								text = "private-memory-limit";
								break;
							}
						}
						if (text != null)
						{
							throw new NotSupportedException("系统未能生成视频缩略图 " + text);
						}
						if (!task.Wait(2000) || !task2.Wait(2000))
						{
							throw new NotSupportedException("视频缩略图输出未能结束。");
						}
						LastWorkerDiagnostic = Encoding.UTF8.GetString(task.GetAwaiter().GetResult());
						if (process.ExitCode != 0)
						{
							throw new NotSupportedException("系统未能生成视频缩略图 " + LastWorkerDiagnostic);
						}
						return DecodeOutput(task2.GetAwaiter().GetResult(), out width, out height);
					}
					finally
					{
						Stop(process);
						Observe(task2);
						Observe(task);
						if (task.Status == TaskStatus.RanToCompletion)
						{
							LastWorkerDiagnostic = Encoding.UTF8.GetString(task.Result);
						}
						try
						{
							process.StandardOutput.Close();
							process.StandardError.Close();
						}
						catch (InvalidOperationException)
						{
						}
					}
				}
			}
			catch (Exception ex2)
			{
				LastFailure = ex2.Message;
				FailureCache.Remember(stamp, DateTime.UtcNow, version);
				return null;
			}
			finally
			{
				Workers.Release();
			}
		}

		internal static byte[] ReadBounded(Stream input, int limit)
		{
			using (MemoryStream memoryStream = new MemoryStream())
			{
				byte[] array = new byte[16384];
				int num;
				while ((num = input.Read(array, 0, array.Length)) > 0)
				{
					if (memoryStream.Length + num > limit)
					{
						throw new InvalidDataException("Video thumbnail output too large");
					}
					memoryStream.Write(array, 0, num);
				}
				return memoryStream.ToArray();
			}
		}

		internal static BitmapSource DecodeOutput(byte[] data, out int width, out int height)
		{
			width = (height = 0);
			if (data == null || data.Length < 41 || data.Length > 3145728)
			{
				throw new InvalidDataException("视频缩略图输出不完整。");
			}
			using (BinaryReader binaryReader = new BinaryReader(new MemoryStream(data, 0, 8, false)))
			{
				width = binaryReader.ReadInt32();
				height = binaryReader.ReadInt32();
			}
			if (width < 0 || height < 0 || width > 100000 || height > 100000)
			{
				throw new InvalidDataException("视频原尺寸无效。");
			}
			byte[] array = new byte[16]
			{
				137, 80, 78, 71, 13, 10, 26, 10, 0, 0,
				0, 13, 73, 72, 68, 82
			};
			for (int i = 0; i < array.Length; i++)
			{
				if (data[8 + i] != array[i])
				{
					throw new InvalidDataException("视频缩略图不是 PNG。");
				}
			}
			uint num = BigEndian(data, 24);
			uint num2 = BigEndian(data, 28);
			if (num < 1 || num2 < 1 || num > 512 || num2 > 512)
			{
				throw new InvalidDataException("视频缩略图尺寸无效。");
			}
			using (MemoryStream streamSource = new MemoryStream(data, 8, data.Length - 8, false))
			{
				BitmapImage bitmapImage = new BitmapImage();
				bitmapImage.BeginInit();
				bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
				bitmapImage.StreamSource = streamSource;
				bitmapImage.EndInit();
				bitmapImage.Freeze();
				if (bitmapImage.PixelWidth != (int)num || bitmapImage.PixelHeight != (int)num2)
				{
					throw new InvalidDataException("视频缩略图尺寸不一致。");
				}
				return bitmapImage;
			}
		}

		private static uint BigEndian(byte[] data, int at)
		{
			return (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);
		}

		private static void Stop(Process process)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill();
					process.WaitForExit(2000);
				}
			}
			catch (InvalidOperationException)
			{
			}
			catch (Win32Exception)
			{
			}
		}

		private static void Observe(Task task)
		{
			try
			{
				task.Wait(2000);
			}
			catch (AggregateException)
			{
			}
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
		private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
		private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr context, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr bitmap);

		[DllImport("ole32.dll")]
		private static extern int PropVariantClear(ref PROPVARIANT value);

		private static int Dimension(IPropertyStore store, uint id)
		{
			var key = new PROPERTYKEY{fmtid=new Guid("64440491-4C8B-11D1-8B70-080036B11A03"),pid=id};
			PROPVARIANT value;
			int value2 = store.GetValue(ref key, out value);
			try
			{
				return (int)((value2 >= 0 && (value.type == 19 || value.type == 3) && value.value <= 100000) ? value.value : 0);
			}
			finally
			{
				PropVariantClear(ref value);
			}
		}

		internal static int Worker(string[] args)
		{
			if (args.Length != 2 || !File.Exists(args[1]))
			{
				return 2;
			}
			if (!string.Equals(Path.GetExtension(args[1]), ".webm", StringComparison.OrdinalIgnoreCase) && ShellWorker(args) == 0)
			{
				return 0;
			}
			return BrowserVideoPoster.Worker(args[1]);
		}

		private static int ShellWorker(string[] args)
		{
			IShellItemImageFactory factory = null;
			IPropertyStore store = null;
			IntPtr bitmap = IntPtr.Zero;
			try
			{
				if (args.Length != 2 || !File.Exists(args[1]))
				{
					return 2;
				}
				Guid iid = typeof(IShellItemImageFactory).GUID;
				SHCreateItemFromParsingName(args[1], IntPtr.Zero, ref iid, out factory);
				Marshal.ThrowExceptionForHR(factory.GetImage(new SIZE
				{
					X = 512,
					Y = 512
				}, 8, out bitmap));
				if (bitmap == IntPtr.Zero)
				{
					return 2;
				}
				BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				bitmapSource.Freeze();
				int value = 0;
				int value2 = 0;
				try
				{
					iid = typeof(IPropertyStore).GUID;
					SHGetPropertyStoreFromParsingName(args[1], IntPtr.Zero, 0, ref iid, out store);
					value = Dimension(store, 3u);
					value2 = Dimension(store, 4u);
				}
				catch (COMException)
				{
				}
				using (MemoryStream memoryStream = new MemoryStream())
				{
					PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
					pngBitmapEncoder.Frames.Add(BitmapFrame.Create(bitmapSource));
					pngBitmapEncoder.Save(memoryStream);
					using (Stream output = Console.OpenStandardOutput())
					{
						using (BinaryWriter binaryWriter = new BinaryWriter(output))
						{
							binaryWriter.Write(value);
							binaryWriter.Write(value2);
							binaryWriter.Write(memoryStream.ToArray());
						}
					}
				}
				return 0;
			}
			catch
			{
				return 2;
			}
			finally
			{
				if (bitmap != IntPtr.Zero)
				{
					DeleteObject(bitmap);
				}
				if (store != null)
				{
					Marshal.ReleaseComObject(store);
				}
				if (factory != null)
				{
					Marshal.ReleaseComObject(factory);
				}
			}
		}
	}
}
