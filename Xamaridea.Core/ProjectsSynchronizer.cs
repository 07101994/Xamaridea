﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xamaridea.Core.Exceptions;
using Xamaridea.Core.Extensions;

namespace Xamaridea.Core
{
	public class ProjectsSynchronizer
	{
		private readonly AndroidProjectTemplateManager _androidProjectTemplateManager = new AndroidProjectTemplateManager ();
		private readonly string _xamarinProjectPath;
		private readonly string _projectName;
		private readonly string _anideExePath;
		private readonly string _sdkPath;

		private bool _grantedPermissionsToChangeMainProject = false;

		public const string ResFolderName = "Resources";

		/// <param name="xamarinProjectPath">Path to root Android xamarin project </param>
		/// <param name="projectName">*.csproj file name</param>
		/// <param name="anideExePath">anide = Android Native IDE (i.e. Android Studio, IDEA, etc (with gradle support))</param>
		public ProjectsSynchronizer (string xamarinProjectPath, string anideExePath, string androidSDKPath = null)
		{
			if (xamarinProjectPath == null)
				throw new ArgumentNullException ("xamarinProjectPath");
			if (anideExePath == null)
				throw new ArgumentNullException ("anideExePath");

			_xamarinProjectPath = Path.GetDirectoryName (xamarinProjectPath);
			_projectName = Path.GetFileNameWithoutExtension (xamarinProjectPath);
			_anideExePath = anideExePath;
			_androidProjectTemplateManager.ExtractTemplateIfNotExtracted ();
			_sdkPath = androidSDKPath;
		}

		public void Sync (string selectedFile = "")
		{
			var resFolder = Path.Combine (_xamarinProjectPath, ResFolderName);
			var ideaProjectDir = _androidProjectTemplateManager.CreateProjectFromTemplate (resFolder, _sdkPath);
			AppendLog ("project dir : {0}", ideaProjectDir);
			//if (!string.IsNullOrEmpty(selectedFile))
			//{
			//    arguments += string.Format(" --line 1 \"{0}\"", selectedFile);
			//}
			Process p;
			if (EnvironmentUtils.IsRunningOnMac ()) {
				p = Process.Start (new ProcessStartInfo (
					//"open",
					//string.Format ("-a '{0}' {1}", _anideExePath, ideaProjectDir.Replace (" ", "\\ "))
					string.Format ("{0}{1}", _anideExePath, "/Contents/MacOS/studio"),
					ideaProjectDir.Replace (" ", "\\ ")
				) {
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardError = true,
					RedirectStandardOutput = true,
				});
			} else {
				string arguments = String.Format ("\"{0}\"", ideaProjectDir);
				p = Process.Start (_anideExePath, arguments); //TODO: specify exact file
			}
			AppendLog("Opening Android Studio");
			p?.WaitForExit();
			AppendLog("Android Studio closed, deleting temp project");
			DeleteProject(ideaProjectDir);
		}

		void DeleteProject (string ideaProjectDir)
		{
			if (Directory.Exists (ideaProjectDir))
				Directory.Delete (ideaProjectDir, true);

		}

		/// <summary>
		/// ANIDE requires those folders and files to be in lower case (and "xml" extension instead of "axml")
		/// </summary>
		public async Task<bool> MakeResourcesSubdirectoriesAndFilesLowercase (Func<Task<bool>> permissionAsker)
		{
			bool madeChanges = false;
			string rootResDir = Path.Combine (_xamarinProjectPath, ResFolderName);

			//we don't need a recursive traversal here since folders in Res directores must not contain subdirectories.
			foreach (var subdir in Directory.GetDirectories(rootResDir)) {
				madeChanges |= await RenameToLowercase (subdir, permissionAsker);
				foreach (var file in Directory.GetFiles(subdir)) {
					madeChanges |= await RenameToLowercase (file, permissionAsker);
				}
			}
			madeChanges |= ChangeAxmlToXmlInCsproj ();
			return madeChanges;
		}

		private async Task<bool> RenameToLowercase (string filePath, Func<Task<bool>> permissionAsker)
		{
			var name = Path.GetFileName (filePath);
			bool isFile = !FileExtensions.IsFolder (filePath);
			bool shouldRenameAxml = isFile && ".axml".Equals (Path.GetExtension (filePath), StringComparison.InvariantCultureIgnoreCase);

			/*TODO: rename Resource.ResSubfolder.Something to Resource.ResSubfolder.something everywhere in code*/

			if (name.HasUppercaseChars () || shouldRenameAxml) {
				if (!_grantedPermissionsToChangeMainProject) {
					//so we noticed that xamarin project has some needed directories in upper case, let's aks user to rename them
					if (!await permissionAsker ()) {
						AppendLog ("Operation cancelled by user");
						throw new OperationCanceledException ("Cancelled by user");
					}
					_grantedPermissionsToChangeMainProject = true;
				}
				if (shouldRenameAxml) {
					AppendLog ("Renaming {0} from axml to xml and to lowercase", name);
					FileExtensions.RenameFileExtensionAndMakeLowercase (filePath, "xml");
					return true;
				} else {
					AppendLog ("Renaming {0} to lowercase", name);
					FileExtensions.RenameFileOrFolderToLowercase (filePath);
					return true;
				}
			}
			return false;
		}

		private bool ChangeAxmlToXmlInCsproj ()
		{
			var csProjPath = Path.Combine (_xamarinProjectPath, _projectName + ".csproj");
			var doc = XDocument.Load (csProjPath);

			var androidResNodes = doc.Descendants ().Where (n => n.Name.LocalName == "AndroidResource").ToArray ();
			bool changed = false;
			foreach (var androidResNode in androidResNodes) {
				var includeAttr = androidResNode.Attribute ("Include");
				if (includeAttr != null) {
					var value = includeAttr.Value ?? "";
					if (value.StartsWith (ResFolderName) && value.EndsWith (".axml", StringComparison.InvariantCultureIgnoreCase)) {
						var newValue = value.Remove (value.Length - "axml".Length, 1); //remove that "a" from axml to become a just xml
						includeAttr.SetValue (newValue);
						changed = true;
					}
				}
			}
			if (changed) {
				try {
					doc.Save (csProjPath);
				} catch (Exception exc) {
					throw new CsprojEditFailedException (csProjPath, exc);
				}
			}
			return changed;
		}

		private void AppendLog (string format, params object[] args)
		{
			Debug.WriteLine (" > " + format, args);
		}
	}
}
