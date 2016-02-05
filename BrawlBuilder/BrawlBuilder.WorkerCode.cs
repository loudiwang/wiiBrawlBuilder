﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace BrawlBuilder
{
	partial class BrawlBuilder
	{
		private bool _pm36patches = false;

		private void buildWorker_DoWork(object sender, DoWorkEventArgs e)
		{
			// Set up wit
			bool showWit = Environment.GetCommandLineArgs().Contains("--show-wit");

			if (!File.Exists(@".\Resources\wit\wit.exe"))
				StopWorker("Unable to find wit executable, stopping build...");
			ProcessStartInfo pStartInfo = new ProcessStartInfo(@".\Resources\wit\wit.exe");
			pStartInfo.CreateNoWindow = !showWit;
			pStartInfo.UseShellExecute = showWit;
			pStartInfo.RedirectStandardOutput = !showWit;

			if (buildWorker.CancellationPending)
			{
				e.Cancel = true;
				return;
			}

			// Check if wit is already running
			Process[] wits = Process.GetProcessesByName("wit");

			if (wits.Length != 0)
			{
				DialogResult result = MessageBox.Show("One or more instances of wit are already running. This could potentially cause issues with the build. Would you like the program to try and kill all instances of wit before continuing?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
				if (result == DialogResult.Yes)
				{
					foreach (Process wit in wits)
					{
						wit.Kill();
						wit.WaitForExit(5000); // Wait for up to 5 seconds for wit to be killed
					}

					// Make sure it worked
					wits = Process.GetProcessesByName("wit");

					if (wits.Length != 0)
						StopWorker("Unable to kill all instances of wit, stopping build...");
				}
			}

			if (buildWorker.CancellationPending)
			{
				e.Cancel = true;
				return;
			}

			// STAGE 1: Analyze GCT
			if (!Analyze())
			{
				e.Cancel = true;
				return;
			}

			// STAGE 2: Extract Brawl
			if (!Extract(pStartInfo))
			{
				e.Cancel = true;
				return;
			}


			// STAGE 3: Verify files
			if (!Verify())
			{
				e.Cancel = true;
				return;
			}


			// STAGE 4: Delete Subspace Emissary
			if (removeSubspace.Checked)
			{
				if (!DeleteSSE())
				{
					e.Cancel = true;
					return;
				}
			}


			// STAGE 5: Apply mod files
			if (modFolder.Text != "")
			{
				if (!CopyModFiles())
				{
					e.Cancel = true;
					return;
				}
			}


			// STAGE 6: Apply banner
			if (customBanner.Checked)
			{
				if (!CopyBanner())
				{
					e.Cancel = true;
					return;
				}
			}

			// STAGE 7: Patch main.dol
			if (gctFile.Text != "" || customID.Checked)
			{
				if (!Patch(pStartInfo))
				{
					e.Cancel = true;
					return;
				}
			}

			// STAGE 8: Build Brawl
			if (!Build(pStartInfo))
			{
				e.Cancel = true;
				return;
			}

			// Clean up
			DeleteBrawlFolder();
		}

		private bool Analyze()
		{
			SetStatus("Analyzing...");

			if (File.Exists(@".\Resources\ProjM36Patches.txt"))
			{
				// First we'll read the patches we are going to make
				string[] actions = { "CHECK", "REMOVE", "PATCH", "TO" };
				string action = "";

				string check = "";
				List<string> remove = new List<string>();
				List<string> patch = new List<string>();
				List<string> to = new List<string>();

				foreach (string s in File.ReadLines(@".\Resources\ProjM36Patches.txt"))
				{
					string line = s.Trim();

					if (line.StartsWith("#"))
						continue; // ignore comments

					if (actions.Contains(line))
					{
						action = line;

						switch (action)
						{
							case "CHECK":
								check = ""; // There should only be 1 check
								break;
							case "REMOVE":
								remove.Add("");
								break;
							case "PATCH":
								patch.Add("");
								break;
							case "TO":
								to.Add("");
								break;
						}

						continue;
					}

					switch (action)
					{
						case "CHECK":
							if (line == "")
								action = "";
							else
								check += line.Replace(" ", "");
							break;
						case "REMOVE":
							if (line == "")
								action = "";
							else
								remove[remove.Count - 1] += line.Replace(" ", "");
							break;
						case "PATCH":
							if (line == "")
								action = "";
							else
								patch[patch.Count - 1] += line.Replace(" ", "");
							break;
						case "TO":
							if (line == "")
								action = "";
							else
								to[to.Count - 1] += line.Replace(" ", "");
							break;
					}
				}

				// Make sure patch has the same number of elements as to
				if (patch.Count != to.Count)
				{
					DialogResult result = MessageBox.Show("ProjM36Patches.txt contains unequal amounts of PATCH and TO statements. Do you want to continue without GCT patching? If your mod is Project M 3.6 or greater, the output ISO may not work without this.", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

					if (result == DialogResult.No)
						buildWorker.CancelAsync();
					else
						return true;

					if (buildWorker.CancellationPending)
						return false;
				}

				// Convert strings of hex values to byte arrays (http://stackoverflow.com/a/321404/1687909)
				byte[] checkBytes = Enumerable.Range(0, check.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(check.Substring(x, 2), 16)).ToArray();
				List<byte[]> removeBytes = new List<byte[]>();
				foreach (string hexBytes in remove)
					removeBytes.Add(Enumerable.Range(0, hexBytes.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(hexBytes.Substring(x, 2), 16)).ToArray());

				List<byte[]> patchBytes = new List<byte[]>();
				foreach (string hexBytes in patch)
					patchBytes.Add(Enumerable.Range(0, hexBytes.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(hexBytes.Substring(x, 2), 16)).ToArray());

				List<byte[]> toBytes = new List<byte[]>();
				foreach (string hexBytes in to)
					toBytes.Add(Enumerable.Range(0, hexBytes.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(hexBytes.Substring(x, 2), 16)).ToArray());

				// Load GCT into memory
				byte[] gctBytes = File.ReadAllBytes(gctFile.Text);

				// Check for 3.6
				int index = SearchBytes(gctBytes, checkBytes);
				if (index > 0)
				{
					DialogResult r = MessageBox.Show("Project M 3.6 codes detected. This may cause issues with the output ISO. Would you like the program to attempt to fix known problem codes?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

					if (r == DialogResult.Yes)
						_pm36patches = true;
					else
						return true;
				}
				else
				{
					return true; // Not 3.6, don't need to patch gct.
				}

				// Do removes
				int successfulRemoves = 0;
				foreach (byte[] removeArr in removeBytes)
				{
					index = SearchBytes(gctBytes, removeArr);
					if (index > 0)
					{
						IEnumerable<byte> before = gctBytes.Take(index);
						IEnumerable<byte> after = gctBytes.Skip(index + removeArr.Length);

						gctBytes = before.Concat(after).ToArray();
						successfulRemoves++;
					}
				}

				// Do patches
				int successfulPatches = 0;
				for (int i = 0; i < patch.Count; i++)
				{
					index = SearchBytes(gctBytes, patchBytes[i]);
					if (index > 0)
					{
						IEnumerable<byte> before = gctBytes.Take(index);
						IEnumerable<byte> after = gctBytes.Skip(index + patchBytes[i].Length);

						gctBytes = before.Concat(toBytes[i].Concat(after)).ToArray();

						successfulPatches++;
					}
				}

				if (successfulRemoves < remove.Count || successfulPatches < patch.Count)
					MessageBox.Show("There were issues fixing known problem codes:\n\nRemoved codes: " + successfulRemoves + "/" + remove.Count + "\nPatched Codes: " + successfulPatches + "/" + patch.Count + "\n\nThere may be issues with the output ISO.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);

				File.WriteAllBytes(@".\Resources\temp.gct", gctBytes);
			}
			else
			{
				DialogResult result = MessageBox.Show("ProjM36Patches.txt not found. Do you want to continue without GCT patching? If your mod is Project M 3.6 or greater, the output ISO may not work without this.", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

				if (result == DialogResult.No)
					buildWorker.CancelAsync();
			}

			if (buildWorker.CancellationPending)
				return false;

			return true;
		}

		// http://stackoverflow.com/a/26880541/1687909
		static int SearchBytes(byte[] haystack, byte[] needle)
		{
			int len = needle.Length;
			int limit = haystack.Length - len;
			for (int i = 0; i <= limit; i++)
			{
				int k = 0;
				for (; k < len; k++)
					if (needle[k] != haystack[i + k])
						break;
				if (k == len) return i;
			}
			return -1;
		}

		private bool Extract(ProcessStartInfo pStartInfo)
		{
			SetStatus("Extracting...");

			bool skipExtraction = false;

			// Check if there is already an extracted Brawl folder
			if (Directory.Exists("ssbb.d") && brawlIso.Text == "")
			{
				skipExtraction = true;
			}
			else if (Directory.Exists("ssbb.d"))
			{
				DialogResult result = MessageBox.Show("There is already an ssbb.d directory. Would you like to skip the extraction and use these files instead?\n\nNote: If you choose 'No', the current ssbb.d directory will be overwritten.", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
				if (result == DialogResult.Yes)
					skipExtraction = true;
				else
					DeleteBrawlFolder();

				if (buildWorker.CancellationPending)
					return false;
			}

			if (!skipExtraction)
			{
				// Extract brawl to ssbb.d folder
				pStartInfo.Arguments = "extract \"" + brawlIso.Text + "\" ssbb.d --psel=DATA -1ovv";

				if (pStartInfo.RedirectStandardOutput)
				{
					if (!DoWitWithProgress(pStartInfo))
						return false;
				}
				else
				{
					// --show-wit option was set
					Process p = Process.Start(pStartInfo);
					p.WaitForExit();

					// Check wit exit code
					if (p.ExitCode != 0)
						StopWorker("Wit closed unexpectedly with exit code " + p.ExitCode + ", stopping build...");

					p.Dispose();
				}
			}

			if (!Directory.Exists("ssbb.d"))
				StopWorker("Extraction failed, stopping build...");

			if (buildWorker.CancellationPending)
			{
				// Since we have been copying files in, ssbb.d is no longer a clean Brawl. Delete it.
				DeleteBrawlFolder();
				return false;
			}

			return true;
		}

		private bool Verify()
		{
			SetStatus("Verifying...");

			if (File.Exists(@".\Resources\BrawlFileList.txt"))
			{
				List<Tuple<string, long>> fileList = new List<Tuple<string, long>>();

				// Parse BrawlFileList.txt
				bool success = true;
				foreach (string s in File.ReadLines(@".\Resources\BrawlFileList.txt"))
				{
					// Each line should have 1 space separating the file path from the file size
					string[] parts = s.Split(' ');

					// Try to convert file size string to long 
					long size;
					if (!long.TryParse(parts[1], out size))
					{
						DialogResult result = MessageBox.Show("Error parsing BrawlFileList.txt, do you want to continue without verifying extracted files?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

						if (result == DialogResult.No)
							buildWorker.CancelAsync();

						success = false;
						break;
					}

					// Add to list
					fileList.Add(Tuple.Create(parts[0], size));
				}

				// Verify brawl files
				if (success)
				{
					foreach (Tuple<string, long> file in fileList)
					{
						if (!File.Exists(@".\ssbb.d\" + file.Item1) || new FileInfo(@".\ssbb.d\" + file.Item1).Length != file.Item2)
						{
							DialogResult result = MessageBox.Show("One or more files are either missing or the wrong size in the extracted Brawl image. Do you still wish to continue?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

							if (result == DialogResult.No)
								buildWorker.CancelAsync();

							break;
						}
					}
				}
			}
			else
			{
				DialogResult result = MessageBox.Show("BrawlFileList.txt not found, do you want to continue without verifying extracted files?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

				if (result == DialogResult.No)
					buildWorker.CancelAsync();
			}

			if (buildWorker.CancellationPending)
				return false;

			return true;
		}

		private bool DeleteSSE()
		{
			if (File.Exists(@".\Resources\SubspaceEmissaryFiles.txt"))
			{
				SetStatus("Deleting SSE...");

				foreach (string file in File.ReadLines(@".\Resources\SubspaceEmissaryFiles.txt"))
				{
					File.Delete(@"ssbb.d\files\" + file);
				}
			}
			else
			{
				SetStatus("Deleting SSE...");
				DialogResult result = MessageBox.Show("SubspaceEmissaryFiles.txt not found. Do you want to continue the build without removing Subspace Emissary?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

				if (result == DialogResult.No)
					buildWorker.CancelAsync();
			}

			if (buildWorker.CancellationPending)
				return false;

			return true;
		}

		private bool CopyModFiles()
		{
			if (Directory.Exists(modFolder.Text))
			{
				SetStatus("Copying...");
				
				// Get mod files in alphabetical order (makes alt stage checking easy)
				string[] modFilesAbsolute = Directory.GetFiles(modFolder.Text, "*", SearchOption.AllDirectories);
				Array.Sort(modFilesAbsolute);

				_progress = 0;
				_progressMax = modFilesAbsolute.Length;

				blinker.RunWorkerAsync();

				Uri relative = new Uri(modFolder.Text);
				foreach (string absoluteFile in modFilesAbsolute)
				{
					// Convert to relative path for easy copying
					Uri fileUri = new Uri(absoluteFile);
					string relativeFile = relative.MakeRelativeUri(fileUri).ToString();

					// relativeFile will still have base folder at this point, so lets remove that
					// First remove leading slash. I think at this point all slashes will be forward slashes, and it wont start with a slash, but w/e
					if (relativeFile.StartsWith("/") || relativeFile.StartsWith("\\"))
						relativeFile = relativeFile.Substring(1);

					// Now remove base folder
					relativeFile = relativeFile.Substring(relativeFile.IndexOfAny(new char[] { '/', '\\' }));

					// Some files need _en added to the end, check in ssbb.d for that
					bool needs_en = false;
					string relativeFile_en;
					if (relativeFile.Contains('.'))
						relativeFile_en = relativeFile.Substring(0, relativeFile.LastIndexOf('.')) + "_en" + relativeFile.Substring(relativeFile.LastIndexOf('.'), relativeFile.Length - relativeFile.LastIndexOf('.'));
					else
						relativeFile_en = relativeFile + "_en";

					// Check brawl files for match with _en, if found then set needs_en to true
					if (File.Exists(@"ssbb.d\files" + relativeFile_en))
						needs_en = true;

					// Perform copy
					Directory.CreateDirectory(@"ssbb.d\files" + Path.GetDirectoryName(relativeFile)); // Just in case it doesn't already exist

					// If we are doing alt stage, then code has been patched and we don't need the _en files
					if (!_pm36patches)
					{
						File.Copy(absoluteFile, needs_en ? @"ssbb.d\files" + relativeFile_en : @"ssbb.d\files" + relativeFile, true);
					}
					else
					{
						File.Copy(absoluteFile, @"ssbb.d\files" + relativeFile, true);
						File.Delete(@"ssbb.d\files" + relativeFile_en); // Get rid of those pesky space-wasting _en files, we don't need em here
					}

					_progress++;

					if (buildWorker.CancellationPending)
					{
						blinker.CancelAsync();

						// Since we have been copying files in, ssbb.d is no longer a clean Brawl. Delete it.
						DeleteBrawlFolder();

						return false;
					}
				}

				// Stop blinker before continuing
				blinker.CancelAsync();
				while (blinker.IsBusy)
					Thread.Sleep(100);

				// If we are doing alt stages, base stage files need to be padded to be the same size as their largest alt stage
				if (_pm36patches)
				{
					string[] stages = Directory.GetFiles(@"ssbb.d\files\stage\melee");

					SetStatus("Padding....");
					_progress = 0;
					_progressMax = stages.Length;
					blinker.RunWorkerAsync();

					foreach (string stage in stages)
					{
						if (!Regex.IsMatch(stage, @"_[A-Z]\.pac$", RegexOptions.IgnoreCase)) // If not an alt stage
						{
							string filename = Path.GetFileNameWithoutExtension(stage);

							// Select all the alt stages for this stage
							IEnumerable<string> altStages = stages.Where(s => Regex.IsMatch(s, filename + @"_[A-Z]\.pac$", RegexOptions.IgnoreCase));

							// Determine the largest one (resharper converted my foreach loop. LINQ is cool.)
							long largest = altStages.Select(altStage => new FileInfo(altStage).Length).Concat(new long[] {0}).Max();

							// If base stage is smaller, add padding to match largest alt stage
							long baseStageSize = new FileInfo(stage).Length;
							if (baseStageSize < largest)
							{
								long padding = largest - baseStageSize;

								using (FileStream stream = new FileStream(stage, FileMode.Append))
								{
									stream.Write(new byte[padding], 0, (int)padding); // Just going to cast to an int since I doubt padding will be > 2GB
								}
							}
						}

						_progress++;

						if (buildWorker.CancellationPending)
						{
							blinker.CancelAsync();

							// Since we have been copying files in, ssbb.d is no longer a clean Brawl. Delete it.
							DeleteBrawlFolder();

							return false;
						}
					}
				}

				// Stop blinker before continuing
				blinker.CancelAsync();
				while (blinker.IsBusy)
					Thread.Sleep(100);
			}
			else
			{
				// Mod folder was selected, but it has been deleted or moved since the initial check
				StopWorker("Mod folder not found, stopping build...");
				return false;
			}

			return true;
		}

		private bool CopyBanner()
		{
			if (File.Exists(banner.Text))
			{
				File.Copy(banner.Text, @"ssbb.d\files\opening.bnr", true);
			}
			else
			{
				DialogResult result = MessageBox.Show("Custom banner file not found, do you want to continue the build without a custom banner?", "Notice", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

				if (result == DialogResult.No)
					buildWorker.CancelAsync();
			}

			if (buildWorker.CancellationPending)
			{
				// Since we have been copying files in, ssbb.d is no longer a clean Brawl. Delete it.
				DeleteBrawlFolder();
				return false;
			}

			return true;
		}

		private bool Patch(ProcessStartInfo pStartInfo)
		{
			string patchArgs = "";
			string gct = gctFile.Text;

			if (_pm36patches)
				gct = @".\Resources\temp.gct";

			if (File.Exists(gct))
			{
				// Determine GCT file size
				long gctSize = new FileInfo(gct).Length;

				// Set up offset
				string offset = "80570000";
				foreach (string arg in Environment.GetCommandLineArgs())
					if (arg.StartsWith("--offset="))
						offset = arg.Substring(9);

				patchArgs += " NEW=TEXT,80001800,10C0 LOAD=80001800,Resources/patch/codehandler.bin XML=Resources/patch/PatchCommon.xml NEW=DATA," + offset + "," + gctSize.ToString("X") + " LOAD=" + offset + ",\"" + gct + "\"";
			}
			else if (gctFile.Text != "")
			{
				// GCT file was selected, but it has been deleted or moved since the initial check
				StopWorker("GCT file not found, stopping build...");
			}

			if (customID.Checked)
			{
				// Need to patch dol to avoid the Please insert disc screen
				string first = gameID.Text.Substring(0, 4);
				string last = gameID.Text.Substring(4);

				if (first != "RSBE")
				{
					// Convert to hex
					first = BitConverter.ToString(Encoding.ASCII.GetBytes(first)).Replace("-", "");
					patchArgs += " 805A14B0=" + first;
				}

				if (last != "01")
				{
					// Convert to hex
					last = BitConverter.ToString(Encoding.ASCII.GetBytes(last)).Replace("-", "");
					patchArgs += " 805A14B8=" + last;
				}
			}

			if (patchArgs != "")
			{
				// Apply patch
				SetStatus("Patching...");

				pStartInfo.Arguments = "dolpatch ssbb.d/sys/main.dol" + patchArgs;
				Process p = Process.Start(pStartInfo);
				p.WaitForExit();

				if (p.ExitCode != 0)
					StopWorker("Wit closed unexpectedly with exit code " + p.ExitCode + ", stopping build...");

				p.Dispose();
			}

			if (buildWorker.CancellationPending)
			{
				// Since we have been copying files in, ssbb.d is no longer a clean Brawl. Delete it.
				DeleteBrawlFolder();
				return false;
			}

			return true;
		}

		private bool Build(ProcessStartInfo pStartInfo)
		{
			SetStatus("Building...");

			bool splitOutput = false;
			if (_saveFileName.EndsWith(".wbfs"))
			{
				DialogResult result = MessageBox.Show("You selected a wbfs output. Do you want to split the output into 4GB chunks for use on FAT32 filesystems? (Assuming the output is greater than 4GB)", "Split output?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

				if (result == DialogResult.Yes)
					splitOutput = true;
			}

			pStartInfo.Arguments = "copy ssbb.d \"" + _saveFileName + "\" -ovv" + (splitOutput ? "z" : "") + (customID.Checked ? " --id=" + gameID.Text : "") + (cutomTitle.Checked ? " --name \"" + gameTitle.Text + "\"" : "");

			if (pStartInfo.RedirectStandardOutput)
			{
				if (!DoWitWithProgress(pStartInfo))
					return false;
			}
			else
			{
				// --show-wit option set
				Process p = Process.Start(pStartInfo);
				p.WaitForExit();

				// Check wit exit code
				if (p.ExitCode != 0)
					StopWorker("Wit closed unexpectedly with exit code " + p.ExitCode + ", stopping build...");

				p.Dispose();
			}

			// Make sure wit created the file
			if (!File.Exists(_saveFileName))
				StopWorker("Build failed, stopping...");

			return true;
		}

		private bool DoWitWithProgress(ProcessStartInfo pStartInfo)
		{
			// Set up blinker
			_progress = 0;
			_progressMax = 100;
			if (blinker.IsBusy)
			{
				blinker.CancelAsync();
				while (blinker.IsBusy)
				{
					Thread.Sleep(100);
				}
			}
			blinker.RunWorkerAsync();

			// Create outside of loop for better performance
			Regex r = new Regex(@"(\d+)%");

			if (pStartInfo.RedirectStandardOutput)
			{
				using (Process p = Process.Start(pStartInfo))
				{
					using (StreamReader reader = p.StandardOutput)
					{
						while (!p.HasExited)
						{
							reader.DiscardBufferedData();

							string curStatus = reader.ReadLine();

							if (curStatus == null)
							{
								// This should only happen if p is killed, but just in case
								p.Kill();
								p.WaitForExit(5000);
								break;
							}
							
							Match m = r.Match(curStatus);

							if (m.Groups.Count > 1)
								_progress = int.Parse(m.Groups[1].Value);

							if (buildWorker.CancellationPending)
							{
								// Stop blinker
								blinker.CancelAsync();

								// Kill process
								p.Kill();
								p.WaitForExit(5000);

								// Didn't finish working, delete ssbb.d
								DeleteBrawlFolder();

								return false;
							}

							Thread.Sleep(100);
						}
					}

					// Check wit exit code
					if (p.ExitCode != 0)
						StopWorker("Wit closed unexpectedly with exit code " + p.ExitCode + ", stopping build...");
				}
			}

			// Stop blinker before continuing
			blinker.CancelAsync();
			while (blinker.IsBusy)
				Thread.Sleep(100);

			if (buildWorker.CancellationPending)
			{
				// Didn't finish working, delete ssbb.d
				DeleteBrawlFolder();

				return false;
			}

			return true;
		}

		private void DeleteBrawlFolder()
		{
			if (Directory.Exists("ssbb.d"))
			{
				try
				{
					Directory.Delete("ssbb.d", true);
				}
				catch (Exception e)
				{
					MessageBox.Show("Unable to delete ssbb.d directory. Deletion failed with error:\n\n" + e.Message, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				}
			}
		}

		private void StopWorker(string message)
		{
			MessageBox.Show(message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			buildWorker.CancelAsync();
		}

		private void SetStatus(string status, bool force = false)
		{
			_curStatus = status;

			if (!_dontTouch || force)
				build.Invoke(new Action(() => build.Text = _curStatus));
		}

		private void buildWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
		{
			// Clean up temp files
			File.Delete(@".\Resources\temp.gct");

			if (_exiting)
				Environment.Exit(1);

			// Re-enable controls
			foreach (Control c in Controls)
			{
				if (c != exit && c != build)
				{
					c.Enabled = (bool)c.Tag;
				}
			}

			// Reset status
			SetStatus((string)build.Tag, true);

			Activate();

			// Show success if builder actually finished
			if (e.Cancelled != true)
				MessageBox.Show("Build Completed!", "Success!", MessageBoxButtons.OK, MessageBoxIcon.Information);
		}
		
		private void blinker_DoWork(object sender, DoWorkEventArgs e)
		{
			string statusBack = _curStatus;
			
			while (true)
			{
				// 'Sleep' for ~2000ms, but cancel faster
				for (int i = 0; i < 20; i++)
				{
					if (blinker.CancellationPending)
						break;
					Thread.Sleep(100);
				}

				if (blinker.CancellationPending)
					break;

				// 'Sleep' for ~4000ms, but also updates
				for (int i = 0; i < 40; i++)
				{
					float percent = (int)((float)_progress / _progressMax * 100);
					SetStatus(percent + "%");
					Thread.Sleep(100);

					if (blinker.CancellationPending)
						break;
				}

				if (blinker.CancellationPending)
					break;

				SetStatus(statusBack);
			}

			SetStatus(statusBack);

			e.Cancel = true;
		}
	}
}