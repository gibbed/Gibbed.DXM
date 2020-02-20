/* Copyright (c) 2020 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NDesk.Options;

namespace Gibbed.DXM.MakeDecalMod
{
	internal class Program
	{
		private static string GetExecutableName()
		{
			return Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase);
		}

		public static void Main(string[] args)
		{
			string decalId = "13";
			bool verbose = false;
			bool showHelp = false;

			var options = new OptionSet()
			{
				{ "i|index=", "set decal index", v => decalId = v },
				{ "v|verbose", "be verbose", v => verbose = v != null },
				{ "h|help", "show this message and exit",  v => showHelp = v != null },
			};

			List<string> extras;
			try
			{
				extras = options.Parse(args);
			}
			catch (OptionException e)
			{
				Console.Write("{0}: ", GetExecutableName());
				Console.WriteLine(e.Message);
				Console.WriteLine("Try `{0} --help' for more information.", GetExecutableName());
				return;
			}

			if (extras.Count < 1 || extras.Count > 2 || showHelp == true)
			{
				Console.WriteLine("Usage: {0} [OPTIONS]+ input_png [output_pak]", GetExecutableName());
				Console.WriteLine("Unpack specified archive.");
				Console.WriteLine();
				Console.WriteLine("Options:");
				options.WriteOptionDescriptions(Console.Out);
				return;
			}

			int decalIndex;
			if (int.TryParse(decalId, out decalIndex) == false)
			{
				Console.WriteLine($"Could not parse decal index '{decalId}'.");
				return;
			}

			if (decalIndex < 0 || decalIndex > 999)
			{
				Console.WriteLine($"Invalid decal index '{decalIndex}.");
				return;
			}

			var inputPath = Path.GetFullPath(extras[0]);
			string outputPath;

			if (extras.Count > 1)
			{
				outputPath = extras[1];
			}
			else
			{
				var outputName = Path.GetFileNameWithoutExtension(inputPath);
				var outputParentPath = Path.GetDirectoryName(inputPath);
				outputPath = Path.Combine(outputParentPath, $"DXM-WindowsNoEditor_Decal_{outputName}_P.pak");
			}

			const Squish.Flags squishFlags = Squish.Flags.DXT5;

			int dxtTotalSize = 0;
			for (int mipIndex = 10; mipIndex >= 0; mipIndex--)
			{
				var mipSize = 1 << mipIndex;
				dxtTotalSize += Squish.GetStorageRequirements(mipSize, mipSize, squishFlags);
			}

			if (dxtTotalSize != 1398128)
			{
				throw new InvalidOperationException();
			}

			var dxtBytes = new byte[dxtTotalSize];
			using (var image = Image.FromFile(inputPath))
			{
				if (image.Width != 1024 || image.Height != 1024)
				{
					Console.WriteLine("[Warning] Original image not 1024x1024, will resize (probably badly).");
				}

				int dxtOffset = 0;
				for (int mipIndex = 10; mipIndex >= 0; mipIndex--)
				{
					var mipSize = 1 << mipIndex;

					var rgbaStride = mipSize * 4;
					var rgbaBytes = new byte[rgbaStride * mipSize];
					using (var bitmap = MaybeResize(image, mipSize, mipSize))
					{
						var area = new Rectangle(0, 0, mipSize, mipSize);
						var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
						var scan = data.Scan0;
						for (int y = 0, o = 0; y < mipSize; y++, scan += data.Stride, o += rgbaStride)
						{
							Marshal.Copy(scan, rgbaBytes, o, rgbaStride);
						}
						bitmap.UnlockBits(data);
					}

					var dxtSize = Squish.GetStorageRequirements(mipSize, mipSize, squishFlags);
					Squish.CompressImage(rgbaBytes, 0, mipSize, mipSize, dxtBytes, dxtOffset, squishFlags);
					dxtOffset += dxtSize;
				}
			}

			var decalIndexBytes = Encoding.ASCII.GetBytes($"{decalIndex:D3}");

			var filenameBytes = Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(outputPath));
			var filenameHash = ComputeMD5(filenameBytes, 0, filenameBytes.Length);

			const int pakEntryHeaderSize = 53;
			const int pakEntryHeaderHashOffset = 28;

			var outputLength = 0;
			var uassetOffset = outputLength;
			outputLength += Resources.UAsset.Length;
			var ubulkHeaderOffset = outputLength;
			outputLength += Resources.UBulkHeader.Length;
			var ubulkDataOffset = outputLength;
			outputLength += dxtBytes.Length;
			var uexpOffset = outputLength;
			outputLength += Resources.UExp.Length;
			var pakIndexOffset = outputLength;
			outputLength += Resources.PakIndex.Length;

			var outputBytes = new byte[outputLength];
			Array.Copy(Resources.UAsset, 0, outputBytes, uassetOffset, Resources.UAsset.Length);
			Array.Copy(Resources.UBulkHeader, 0, outputBytes, ubulkDataOffset, Resources.UBulkHeader.Length);
			Array.Copy(dxtBytes, 0, outputBytes, ubulkDataOffset, dxtBytes.Length);
			Array.Copy(Resources.UExp, 0, outputBytes, uexpOffset, Resources.UExp.Length);
			Array.Copy(Resources.PakIndex, 0, outputBytes, pakIndexOffset, Resources.PakIndex.Length);

			Array.Copy(filenameHash, 0, outputBytes, uassetOffset + pakEntryHeaderSize + 93, filenameHash.Length);
			Array.Copy(decalIndexBytes, 0, outputBytes, uassetOffset + pakEntryHeaderSize + 243, decalIndexBytes.Length);
			Array.Copy(decalIndexBytes, 0, outputBytes, uassetOffset + pakEntryHeaderSize + 349, decalIndexBytes.Length);

			var uassetHash = ComputeSHA1(outputBytes, uassetOffset + pakEntryHeaderSize, Resources.UAsset.Length - pakEntryHeaderSize);
			var ubulkHash = ComputeSHA1(outputBytes, ubulkDataOffset, dxtBytes.Length);
			var uexpHash = ComputeSHA1(outputBytes, uexpOffset + pakEntryHeaderSize, Resources.UExp.Length - pakEntryHeaderSize);

			Array.Copy(uassetHash, 0, outputBytes, uassetOffset + pakEntryHeaderHashOffset, uassetHash.Length);
			Array.Copy(ubulkHash, 0, outputBytes, ubulkHeaderOffset + pakEntryHeaderHashOffset, ubulkHash.Length);
			Array.Copy(uexpHash, 0, outputBytes, uexpOffset + pakEntryHeaderHashOffset, uexpHash.Length);

			Array.Copy(decalIndexBytes, 0, outputBytes, pakIndexOffset + 74, decalIndexBytes.Length);
			Array.Copy(uassetHash, 0, outputBytes, pakIndexOffset + 113, uassetHash.Length);
			Array.Copy(decalIndexBytes, 0, outputBytes, pakIndexOffset + 151, decalIndexBytes.Length);
			Array.Copy(ubulkHash, 0, outputBytes, pakIndexOffset + 189, ubulkHash.Length);
			Array.Copy(decalIndexBytes, 0, outputBytes, pakIndexOffset + 227, decalIndexBytes.Length);
			Array.Copy(uexpHash, 0, outputBytes, pakIndexOffset + 264, uexpHash.Length);

			var pakIndexHash = ComputeSHA1(outputBytes, pakIndexOffset, 289);
			Array.Copy(pakIndexHash, 0, outputBytes, pakIndexOffset + 314, pakIndexHash.Length);

			File.WriteAllBytes(outputPath, outputBytes);
		}

		private static byte[] ComputeMD5(byte[] bytes, int offset, int count)
		{
			using (var sha1 = MD5.Create())
			{
				return (byte[])sha1.ComputeHash(bytes, offset, count).Clone();
			}
		}

		private static byte[] ComputeSHA1(byte[] bytes, int offset, int count)
		{
			using (var sha1 = SHA1.Create())
			{
				return (byte[])sha1.ComputeHash(bytes, offset, count).Clone();
			}
		}

		private static Bitmap MaybeResize(Image image, int width, int height)
		{
			if (image.Width == width && image.Height == height)
			{
				return new Bitmap(image);
			}

			var area = new Rectangle(0, 0, width, height);
			var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
			bitmap.SetResolution(image.HorizontalResolution, image.VerticalResolution);
			using (var graphics = Graphics.FromImage(bitmap))
			{
				graphics.CompositingMode = CompositingMode.SourceCopy;
				graphics.CompositingQuality = CompositingQuality.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.SmoothingMode = SmoothingMode.HighQuality;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				using (var wrapMode = new ImageAttributes())
				{
					wrapMode.SetWrapMode(WrapMode.TileFlipXY);
					graphics.DrawImage(image, area, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
				}
			}
			return bitmap;
		}
	}
}
