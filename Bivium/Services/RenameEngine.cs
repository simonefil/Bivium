using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Bivium.Models;

namespace Bivium.Services
{
    /// <summary>
    /// Static engine for applying rename methods to filenames
    /// </summary>
    public static class RenameEngine
    {
        #region Private Variables

        /// <summary>
        /// Random number generator for Rand tag
        /// </summary>
        private static Random s_random = new Random();

        /// <summary>
        /// Invalid filename characters for validation
        /// </summary>
        private static char[] s_invalidChars = Path.GetInvalidFileNameChars();

        #endregion

        #region Public Methods

        /// <summary>
        /// Generates preview items by applying all methods sequentially to each entry
        /// </summary>
        /// <param name="entries">File system entries to rename</param>
        /// <param name="methodStack">Ordered list of rename methods to apply</param>
        /// <returns>List of preview items with computed new names and conflict flags</returns>
        public static List<RenamePreviewItem> GeneratePreview(List<FileSystemEntry> entries, List<RenameMethod> methodStack)
        {
            List<RenamePreviewItem> items = new List<RenamePreviewItem>();

            for (int i = 0; i < entries.Count; i++)
            {
                FileSystemEntry entry = entries[i];
                string currentName = entry.Name;
                string parentFolder = Path.GetFileName(Path.GetDirectoryName(entry.FullPath)) ?? "";

                // Apply each method in sequence
                for (int m = 0; m < methodStack.Count; m++)
                {
                    currentName = ApplyMethod(currentName, methodStack[m], i, entry.LastModified, parentFolder);
                }

                RenamePreviewItem item = new RenamePreviewItem();
                item.OriginalName = entry.Name;
                item.OriginalFullPath = entry.FullPath;
                item.NewName = currentName;
                item.HasError = currentName.IndexOfAny(s_invalidChars) >= 0 || string.IsNullOrWhiteSpace(currentName);
                items.Add(item);
            }

            // Detect conflicts
            DetectConflicts(items);

            return items;
        }

        /// <summary>
        /// Applies a single rename method to a filename
        /// </summary>
        /// <param name="fileName">Current filename</param>
        /// <param name="method">Rename method to apply</param>
        /// <param name="fileIndex">Index of the file in the list (for Inc tag)</param>
        /// <param name="lastModified">File last modified date (for Date tag)</param>
        /// <param name="parentFolder">Parent folder name (for Folder tag)</param>
        /// <returns>New filename after method applied</returns>
        public static string ApplyMethod(string fileName, RenameMethod method, int fileIndex, DateTime lastModified, string parentFolder)
        {
            string result = fileName;

            if (method.MethodType == RenameMethodType.Replace)
            {
                result = ApplyReplace(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.Add)
            {
                result = ApplyAdd(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.Remove)
            {
                result = ApplyRemove(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.NewCase)
            {
                result = ApplyNewCase(fileName, method);
            }
            else if (method.MethodType == RenameMethodType.NewName)
            {
                result = ApplyNewName(fileName, method, fileIndex, lastModified, parentFolder);
            }
            else if (method.MethodType == RenameMethodType.Trim)
            {
                result = ApplyTrim(fileName, method);
            }

            return result;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Splits filename into name and extension parts
        /// </summary>
        /// <param name="fileName">Full filename</param>
        /// <param name="name">Output: name without extension</param>
        /// <param name="ext">Output: extension without dot</param>
        private static void SplitFileName(string fileName, out string name, out string ext)
        {
            string extension = Path.GetExtension(fileName);
            if (extension.Length > 0)
            {
                // Remove leading dot
                ext = extension.Substring(1);
                name = fileName.Substring(0, fileName.Length - extension.Length);
            }
            else
            {
                ext = "";
                name = fileName;
            }
        }

        /// <summary>
        /// Reassembles filename from name and extension parts
        /// </summary>
        /// <param name="name">Name without extension</param>
        /// <param name="ext">Extension without dot</param>
        /// <returns>Combined filename</returns>
        private static string JoinFileName(string name, string ext)
        {
            if (string.IsNullOrEmpty(ext))
            {
                return name;
            }
            return name + "." + ext;
        }

        /// <summary>
        /// Applies find and replace method
        /// </summary>
        private static string ApplyReplace(string fileName, RenameMethod method)
        {
            string result = fileName;

            if (string.IsNullOrEmpty(method.SearchText))
            {
                return result;
            }

            if (method.UseRegex)
            {
                try
                {
                    RegexOptions options = RegexOptions.None;
                    if (!method.CaseSensitive)
                    {
                        options = RegexOptions.IgnoreCase;
                    }
                    string replacement = method.ReplaceText ?? "";
                    result = Regex.Replace(fileName, method.SearchText, replacement, options);
                }
                catch
                {
                    // Invalid regex, return unchanged
                    result = fileName;
                }
            }
            else
            {
                if (method.CaseSensitive)
                {
                    result = fileName.Replace(method.SearchText, method.ReplaceText);
                }
                else
                {
                    result = ReplaceCaseInsensitive(fileName, method.SearchText, method.ReplaceText);
                }
            }

            return result;
        }

        /// <summary>
        /// Case-insensitive string replace
        /// </summary>
        private static string ReplaceCaseInsensitive(string input, string search, string replacement)
        {
            string result = input;
            int index = 0;

            while (true)
            {
                int found = result.IndexOf(search, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    break;
                }
                result = result.Substring(0, found) + replacement + result.Substring(found + search.Length);
                index = found + replacement.Length;
            }

            return result;
        }

        /// <summary>
        /// Applies text insertion method
        /// </summary>
        private static string ApplyAdd(string fileName, RenameMethod method)
        {
            string result = fileName;

            if (string.IsNullOrEmpty(method.InsertText))
            {
                return result;
            }

            SplitFileName(fileName, out string name, out string ext);

            int pos = method.InsertPosition;
            if (method.FromEnd)
            {
                pos = name.Length - pos;
            }

            // Clamp position
            if (pos < 0)
            {
                pos = 0;
            }
            if (pos > name.Length)
            {
                pos = name.Length;
            }

            name = name.Insert(pos, method.InsertText);
            result = JoinFileName(name, ext);

            return result;
        }

        /// <summary>
        /// Applies character removal method
        /// </summary>
        private static string ApplyRemove(string fileName, RenameMethod method)
        {
            string result = fileName;

            SplitFileName(fileName, out string name, out string ext);

            if (method.RemoveByPattern)
            {
                // Remove by pattern
                if (string.IsNullOrEmpty(method.RemovePattern))
                {
                    return result;
                }

                if (method.RemovePatternUseRegex)
                {
                    try
                    {
                        RegexOptions options = RegexOptions.None;
                        if (!method.RemovePatternCaseSensitive)
                        {
                            options = RegexOptions.IgnoreCase;
                        }
                        name = Regex.Replace(name, method.RemovePattern, "", options);
                    }
                    catch
                    {
                        // Invalid regex, return unchanged
                        return result;
                    }
                }
                else
                {
                    if (method.RemovePatternCaseSensitive)
                    {
                        name = name.Replace(method.RemovePattern, "");
                    }
                    else
                    {
                        name = ReplaceCaseInsensitive(name, method.RemovePattern, "");
                    }
                }
            }
            else
            {
                // Remove by position
                int start = method.RemoveStartIndex;
                int count = method.RemoveCount;

                if (method.RemoveFromEnd)
                {
                    start = name.Length - start - count;
                }

                if (start < 0)
                {
                    count = count + start;
                    start = 0;
                }
                if (start >= name.Length || count <= 0)
                {
                    return result;
                }
                if (start + count > name.Length)
                {
                    count = name.Length - start;
                }

                name = name.Remove(start, count);
            }

            result = JoinFileName(name, ext);
            return result;
        }

        /// <summary>
        /// Applies case change method
        /// </summary>
        private static string ApplyNewCase(string fileName, RenameMethod method)
        {
            SplitFileName(fileName, out string name, out string ext);

            // Apply case change based on scope
            if (method.CaseScope == 0 || method.CaseScope == 2)
            {
                // Name
                name = ChangeCase(name, method.CaseMode);
            }
            if (method.CaseScope == 1 || method.CaseScope == 2)
            {
                // Extension
                ext = ChangeCase(ext, method.CaseMode);
            }

            string result = JoinFileName(name, ext);
            return result;
        }

        /// <summary>
        /// Changes the case of a string
        /// </summary>
        /// <param name="text">Input text</param>
        /// <param name="mode">0=lower, 1=upper, 2=title</param>
        /// <returns>Text with changed case</returns>
        private static string ChangeCase(string text, int mode)
        {
            string result = text;

            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            if (mode == 0)
            {
                result = text.ToLowerInvariant();
            }
            else if (mode == 1)
            {
                result = text.ToUpperInvariant();
            }
            else if (mode == 2)
            {
                TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
                result = textInfo.ToTitleCase(text.ToLowerInvariant());
            }

            return result;
        }

        /// <summary>
        /// Applies new name pattern with tag substitution
        /// </summary>
        private static string ApplyNewName(string fileName, RenameMethod method, int fileIndex, DateTime lastModified, string parentFolder)
        {
            SplitFileName(fileName, out string originalName, out string originalExt);

            string pattern = method.NamePattern;
            if (string.IsNullOrEmpty(pattern))
            {
                return fileName;
            }

            string result = "";
            int pos = 0;

            while (pos < pattern.Length)
            {
                // Look for tag opening
                int tagStart = pattern.IndexOf('<', pos);
                if (tagStart < 0)
                {
                    // No more tags, append the rest
                    result = result + pattern.Substring(pos);
                    break;
                }

                // Append text before tag
                if (tagStart > pos)
                {
                    result = result + pattern.Substring(pos, tagStart - pos);
                }

                // Find tag closing
                int tagEnd = pattern.IndexOf('>', tagStart);
                if (tagEnd < 0)
                {
                    // Unclosed tag, append as literal
                    result = result + pattern.Substring(tagStart);
                    break;
                }

                // Extract tag content
                string tagContent = pattern.Substring(tagStart + 1, tagEnd - tagStart - 1);
                string tagValue = ResolveTag(tagContent, originalName, originalExt, fileIndex, lastModified, parentFolder);
                result = result + tagValue;

                pos = tagEnd + 1;
            }

            return result;
        }

        /// <summary>
        /// Resolves a tag to its value
        /// </summary>
        /// <param name="tagContent">Tag content without angle brackets</param>
        /// <param name="originalName">Original filename without extension</param>
        /// <param name="originalExt">Original extension without dot</param>
        /// <param name="fileIndex">File index in the list</param>
        /// <param name="lastModified">File last modified date</param>
        /// <param name="parentFolder">Parent folder name</param>
        /// <returns>Resolved tag value</returns>
        private static string ResolveTag(string tagContent, string originalName, string originalExt, int fileIndex, DateTime lastModified, string parentFolder)
        {
            string result = "<" + tagContent + ">";

            if (tagContent == "Name")
            {
                result = originalName;
            }
            else if (tagContent == "Ext")
            {
                result = originalExt;
            }
            else if (tagContent == "Folder")
            {
                result = parentFolder;
            }
            else if (tagContent.StartsWith("Inc:"))
            {
                result = ResolveIncTag(tagContent, fileIndex);
            }
            else if (tagContent.StartsWith("Date:"))
            {
                result = ResolveDateTag(tagContent, lastModified);
            }
            else if (tagContent.StartsWith("Rand:"))
            {
                result = ResolveRandTag(tagContent);
            }

            return result;
        }

        /// <summary>
        /// Resolves Inc tag: Inc:start:step:pad
        /// </summary>
        private static string ResolveIncTag(string tagContent, int fileIndex)
        {
            string result = "";

            // Remove "Inc:" prefix
            string parameters = tagContent.Substring(4);
            string[] parts = parameters.Split(':');

            int start = 1;
            int step = 1;
            int pad = 1;

            if (parts.Length >= 1)
            {
                int.TryParse(parts[0], out start);
            }
            if (parts.Length >= 2)
            {
                int.TryParse(parts[1], out step);
            }
            if (parts.Length >= 3)
            {
                int.TryParse(parts[2], out pad);
            }

            int value = start + (fileIndex * step);
            result = value.ToString().PadLeft(pad, '0');

            return result;
        }

        /// <summary>
        /// Resolves Date tag: Date:format
        /// </summary>
        private static string ResolveDateTag(string tagContent, DateTime lastModified)
        {
            string result = "";

            // Remove "Date:" prefix
            string format = tagContent.Substring(5);

            try
            {
                result = lastModified.ToString(format, CultureInfo.InvariantCulture);
            }
            catch
            {
                // Invalid format, return tag as-is
                result = "<" + tagContent + ">";
            }

            return result;
        }

        /// <summary>
        /// Resolves Rand tag: Rand:min:max
        /// </summary>
        private static string ResolveRandTag(string tagContent)
        {
            string result = "";

            // Remove "Rand:" prefix
            string parameters = tagContent.Substring(5);
            string[] parts = parameters.Split(':');

            int min = 0;
            int max = 100;

            if (parts.Length >= 1)
            {
                int.TryParse(parts[0], out min);
            }
            if (parts.Length >= 2)
            {
                int.TryParse(parts[1], out max);
            }

            if (max <= min)
            {
                max = min + 1;
            }

            result = s_random.Next(min, max + 1).ToString();

            return result;
        }

        /// <summary>
        /// Applies trim method to remove characters from edges
        /// </summary>
        private static string ApplyTrim(string fileName, RenameMethod method)
        {
            SplitFileName(fileName, out string name, out string ext);

            string trimSource = method.TrimCharacters ?? " ";
            char[] trimChars = trimSource.ToCharArray();
            if (trimChars.Length == 0)
            {
                trimChars = new char[] { ' ' };
            }

            // Apply trim based on scope
            if (method.TrimScope == 0 || method.TrimScope == 2)
            {
                // Name
                name = TrimString(name, trimChars, method.TrimLocation);
            }
            if (method.TrimScope == 1 || method.TrimScope == 2)
            {
                // Extension
                ext = TrimString(ext, trimChars, method.TrimLocation);
            }

            string result = JoinFileName(name, ext);
            return result;
        }

        /// <summary>
        /// Trims characters from a string at specified location
        /// </summary>
        /// <param name="text">Input text</param>
        /// <param name="chars">Characters to trim</param>
        /// <param name="location">0=start, 1=end, 2=both</param>
        /// <returns>Trimmed text</returns>
        private static string TrimString(string text, char[] chars, int location)
        {
            string result = text;

            if (location == 0)
            {
                result = text.TrimStart(chars);
            }
            else if (location == 1)
            {
                result = text.TrimEnd(chars);
            }
            else
            {
                result = text.Trim(chars);
            }

            return result;
        }

        /// <summary>
        /// Detects name conflicts (duplicate new names) in the preview list
        /// </summary>
        /// <param name="items">Preview items to check</param>
        private static void DetectConflicts(List<RenamePreviewItem> items)
        {
            // Reset all conflict flags
            for (int i = 0; i < items.Count; i++)
            {
                items[i].HasConflict = false;
            }

            // Use case-insensitive comparison on Windows, case-sensitive on Linux
            StringComparison comparison = StringComparison.Ordinal;
            if (OperatingSystem.IsWindows())
            {
                comparison = StringComparison.OrdinalIgnoreCase;
            }

            // Compare each pair
            for (int i = 0; i < items.Count; i++)
            {
                for (int j = i + 1; j < items.Count; j++)
                {
                    // Only check items in the same directory
                    string dirI = Path.GetDirectoryName(items[i].OriginalFullPath);
                    string dirJ = Path.GetDirectoryName(items[j].OriginalFullPath);

                    if (string.Equals(dirI, dirJ, comparison))
                    {
                        if (string.Equals(items[i].NewName, items[j].NewName, comparison))
                        {
                            items[i].HasConflict = true;
                            items[j].HasConflict = true;
                        }
                    }
                }
            }
        }

        #endregion
    }
}
