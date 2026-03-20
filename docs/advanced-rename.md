# Advanced Rename

Advanced Rename is a batch file renaming tool. It allows renaming multiple files at once by composing an ordered stack of rename methods that are applied sequentially to each filename. A live preview shows the result of every change before any file is actually renamed on disk.

---

## Table of Contents

1. [Accessing Advanced Rename](#accessing-advanced-rename)
2. [File Selection](#file-selection)
3. [The Interface](#the-interface)
4. [Rename Methods](#rename-methods)
   - [Replace](#replace)
   - [Add](#add)
   - [Remove](#remove)
   - [New Case](#new-case)
   - [New Name](#new-name)
   - [Trim](#trim)
5. [Method Stack](#method-stack)
6. [Live Preview](#live-preview)
7. [Conflicts and Errors](#conflicts-and-errors)
8. [Executing the Rename](#executing-the-rename)
9. [Keyboard Shortcuts](#keyboard-shortcuts)

---

## Accessing Advanced Rename

| Method | How |
|---|---|
| Keyboard shortcut | Select files, then press **Ctrl+F2** |
| Context menu | Right-click on a selection, choose **Advanced Rename...** |
| Menu bar | **Edit** > **Advanced Rename...** |

---

## File Selection

Advanced Rename operates on files, not directories. The way files are collected depends on the selection:

### Multiple files selected

All **files** from the selection are included. Directories in the selection are skipped.

### Single directory selected

All files inside that directory are collected **recursively**, including files in all subdirectories at any depth. Useful for batch-renaming an entire folder tree (e.g. renaming all photos inside a multi-level album structure).

### Single file selected

Advanced Rename opens with just that single file. Useful for testing a rename pattern before applying it to a larger set.

---

## The Interface

The window is split into two areas:

**File list** (left) — a table showing the **Original Name** and the computed **New Name** for each file. The table scrolls when the file list is large. Rows are color-coded:

| Appearance | Meaning |
|---|---|
| Normal | Name unchanged |
| Original name struck through, new name highlighted | Name will change |
| New name in red | Conflict — two or more files would get the same name |
| New name struck through in gray | Error — the new name contains invalid characters |

**Configuration panel** (right) — where you select a method type from the dropdown, configure its parameters, and click **Add** to commit it to the method stack. Below the parameters, the method stack lists all committed methods with controls to reorder or remove them.

**Status bar** (bottom) — shows the file count, number of changed names, and any conflicts or errors. Contains the **Rename** and **Cancel** buttons.

The window can be dragged by its title bar and resized from the bottom-right corner.

---

## Rename Methods

Six method types are available. Each operates on the filename and produces a new filename. Methods can be combined in any order and quantity by adding them to the method stack.

### Replace

Find and replace text within the filename.

| Parameter | Default | Description |
|---|---|---|
| Search | (empty) | The text to find. Required. |
| Replace | (empty) | The replacement text. Leave empty to delete matches. |
| Case sensitive | Off | When on, `Photo` does not match `photo`. |
| Use regex | Off | When on, the search field is a regular expression. The replace field can use substitution groups (`$1`, `$2`, etc.). |

The entire filename including extension is searched. All occurrences are replaced.

**Examples:**

| Files | Search | Replace | Options | Result |
|---|---|---|---|---|
| `IMG_001.jpg` | `IMG_` | `Photo_` | | `Photo_001.jpg` |
| `Report Final (2).docx` | ` (2)` | | | `Report Final.docx` |
| `photo.JPG` | `.jpg` | `.jpeg` | Case sensitive: Off | `photo.jpeg` |
| `IMG_2024_01_15.jpg` | `_` | `-` | | `IMG-2024-01-15.jpg` |
| `file001.txt` | `(\d+)` | `_$1_` | Regex: On | `file_001_.txt` |
| `backup_20240115.tar.gz` | `\d{8}` | `latest` | Regex: On | `backup_latest.tar.gz` |
| `CamelCaseFile.txt` | `([a-z])([A-Z])` | `$1_$2` | Regex: On | `Camel_Case_File.txt` |

**Regex notes:**

If regex is enabled and the pattern is invalid, the preview shows the filename unchanged and an error message appears below the parameters.

Common patterns: `\d` (digit), `\w` (word character), `\s` (whitespace), `.` (any character), `+` (one or more), `*` (zero or more), `?` (optional), `^` (start), `$` (end). Named groups `(?<name>...)` can be referenced as `${name}` in the replacement.

---

### Add

Insert text at a specific position in the filename.

| Parameter | Default | Description |
|---|---|---|
| Text | (empty) | The text to insert. Required. |
| Position | 0 | Character position for insertion (0-based). |
| From end | Off | Count the position from the end of the name instead of the beginning. |

The insertion operates on the **name part only** — the extension is preserved unchanged.

- Position 0 = before the first character (prepend)
- Position equal to the name length = after the last character (append)
- Position 0 from end = append at the very end of the name
- Position 3 from end = insert 3 characters before the end of the name

**Examples:**

| Files | Text | Position | From end | Result |
|---|---|---|---|---|
| `report.pdf` | `2024_` | 0 | Off | `2024_report.pdf` |
| `photo.jpg` | `_final` | 5 | Off | `photo_final.jpg` |
| `document.docx` | `_v2` | 0 | On | `document_v2.docx` |
| `image.png` | `prefix_` | 0 | Off | `prefix_image.png` |

---

### Remove

Remove characters from the filename by position or by matching a pattern. The **Mode** dropdown selects between the two approaches.

#### By Position

| Parameter | Default | Description |
|---|---|---|
| Start | 0 | Starting position (0-based). |
| Count | 0 | Number of characters to remove. |
| From end | Off | Count from the end of the name. |

Operates on the **name part only** — the extension is preserved.

When "From end" is enabled: start=0, count=3 removes the last 3 characters of the name.

**Examples:**

| Files | Start | Count | From end | Result |
|---|---|---|---|---|
| `IMG_0001.jpg` | 0 | 4 | Off | `0001.jpg` |
| `document_v2.pdf` | 0 | 3 | On | `document.pdf` |
| `2024-01-15_photo.png` | 0 | 11 | Off | `photo.png` |

#### By Pattern

| Parameter | Default | Description |
|---|---|---|
| Pattern | (empty) | The text or regex to match. All matches are removed. Required. |
| Case sensitive | Off | When on, matching is case-sensitive. |
| Use regex | Off | When on, the pattern is a regular expression. |

Operates on the **name part only** — the extension is preserved. All occurrences are removed.

**Examples:**

| Files | Pattern | Options | Result |
|---|---|---|---|
| `photo (1).jpg` | ` (1)` | | `photo.jpg` |
| `FILE_name_FINAL.txt` | `_final` | Case sensitive: Off | `FILE_name.txt` |
| `img_2024_01_15.png` | `_\d{2}` | Regex: On | `img_2024.png` |
| `report---draft.pdf` | `-+` | Regex: On | `reportdraft.pdf` |

---

### New Case

Change the capitalization of the filename.

| Parameter | Options | Default | Description |
|---|---|---|---|
| Mode | lowercase, UPPERCASE, Title Case | lowercase | The target case style. |
| Scope | Name only, Extension only, Full name | Name only | Which part of the filename to transform. |

**Modes:**

| Mode | Example (Name only) |
|---|---|
| lowercase | `My Photo.JPG` → `my photo.JPG` |
| UPPERCASE | `My Photo.jpg` → `MY PHOTO.jpg` |
| Title Case | `my PHOTO file.jpg` → `My Photo File.jpg` |

**Scopes:**

| Scope | What it affects | Example (UPPERCASE) |
|---|---|---|
| Name only | Everything before the last dot | `photo.jpg` → `PHOTO.jpg` |
| Extension only | Everything after the last dot | `photo.jpg` → `photo.JPG` |
| Full name | Entire filename | `photo.jpg` → `PHOTO.JPG` |

**Examples:**

| Files | Mode | Scope | Result |
|---|---|---|---|
| `My Vacation Photo.JPG` | lowercase | Full name | `my vacation photo.jpg` |
| `report_final.pdf` | UPPERCASE | Name only | `REPORT_FINAL.pdf` |
| `IMG_0001.Jpeg` | lowercase | Extension only | `IMG_0001.jpeg` |
| `hello world.txt` | Title Case | Name only | `Hello World.txt` |

---

### New Name

Replace the entire filename with a new pattern built from template tags. This is the most powerful method — it allows constructing entirely new filenames using dynamic values like incrementing counters, dates, folder names, and random numbers.

| Parameter | Default | Description |
|---|---|---|
| Pattern | `<Name>.<Ext>` | The template pattern. Mix literal text with tags. |

#### Tags

Tags are enclosed in angle brackets and are replaced with dynamic values.

#### `<Name>`

The original filename without extension.

| Original | Pattern | Result |
|---|---|---|
| `photo.jpg` | `<Name>` | `photo` |
| `IMG_0001.png` | `backup_<Name>` | `backup_IMG_0001` |

#### `<Ext>`

The original file extension without the dot.

| Original | Pattern | Result |
|---|---|---|
| `photo.jpg` | `<Name>.<Ext>` | `photo.jpg` |
| `document.tar.gz` | `<Name>.<Ext>` | `document.tar.gz` |

#### `<Folder>`

The name of the parent directory containing the file.

| File path | Pattern | Result |
|---|---|---|
| `/photos/vacation/img.jpg` | `<Folder>_<Name>.<Ext>` | `vacation_img.jpg` |
| `/docs/reports/q1.pdf` | `<Folder>-<Name>.<Ext>` | `reports-q1.pdf` |

#### `<Inc:start:step:pad>`

An incrementing counter. Each file in the list receives the next value in the sequence.

| Part | Description | Default |
|---|---|---|
| `start` | Starting number for the first file | 1 |
| `step` | Increment between consecutive files | 1 |
| `pad` | Minimum number of digits, padded with leading zeros | 1 |

The first file gets `start`, the second gets `start + step`, the third gets `start + 2*step`, and so on.

**Padding:** with `pad=3`, the number 1 becomes `001`, 10 becomes `010`, 100 stays `100`, 1000 stays `1000`.

| Files (3) | Pattern | File 1 | File 2 | File 3 |
|---|---|---|---|---|
| `a.jpg, b.jpg, c.jpg` | `IMG_<Inc:1:1:4>.<Ext>` | `IMG_0001.jpg` | `IMG_0002.jpg` | `IMG_0003.jpg` |
| `a.jpg, b.jpg, c.jpg` | `<Inc:0:1:1>_<Name>.<Ext>` | `0_a.jpg` | `1_b.jpg` | `2_c.jpg` |
| `a.jpg, b.jpg, c.jpg` | `photo_<Inc:10:10:1>.<Ext>` | `photo_10.jpg` | `photo_20.jpg` | `photo_30.jpg` |
| `a.jpg, b.jpg, c.jpg` | `<Inc:100:5:3>.<Ext>` | `100.jpg` | `105.jpg` | `110.jpg` |
| `a.jpg, b.jpg, c.jpg` | `item<Inc:1:1:3>.<Ext>` | `item001.jpg` | `item002.jpg` | `item003.jpg` |

#### `<Date:format>`

The file's **last modified date**, formatted with a date format string.

| Format | Description | Example output |
|---|---|---|
| `yyyy` | 4-digit year | `2024` |
| `yy` | 2-digit year | `24` |
| `MM` | Month (01-12) | `03` |
| `dd` | Day (01-31) | `15` |
| `HH` | Hour 24h (00-23) | `14` |
| `mm` | Minutes (00-59) | `30` |
| `ss` | Seconds (00-59) | `45` |
| `yyyy-MM-dd` | ISO date | `2024-03-15` |
| `yyyyMMdd` | Compact date | `20240315` |
| `yyyyMMdd_HHmmss` | Date and time | `20240315_143045` |

| Files | Pattern | Result (modified 2024-03-15 14:30) |
|---|---|---|
| `photo.jpg` | `<Date:yyyy-MM-dd>_<Name>.<Ext>` | `2024-03-15_photo.jpg` |
| `scan.pdf` | `<Date:yyyyMMdd>.<Ext>` | `20240315.pdf` |
| `log.txt` | `<Name>_<Date:yyyy>.<Ext>` | `log_2024.txt` |

If the format string is invalid, the tag is left as-is in the output.

#### `<Rand:min:max>`

A random integer between `min` and `max` (inclusive). Default range: 0 to 100.

| Files | Pattern | Possible result |
|---|---|---|
| `file.txt` | `<Name>_<Rand:1000:9999>.<Ext>` | `file_4827.txt` |
| `doc.pdf` | `<Rand:1:100>_<Name>.<Ext>` | `42_doc.pdf` |

Random numbers are not guaranteed to be unique — check the preview for conflicts.

#### Combining Tags

Tags can be freely combined with literal text:

| Pattern | Description |
|---|---|
| `<Name>.<Ext>` | Identity — reproduces the original filename |
| `<Folder>_<Name>_<Inc:1:1:3>.<Ext>` | Prepend folder name, append counter |
| `<Date:yyyy-MM-dd>_<Inc:1:1:2>.<Ext>` | Date + counter, discard original name |
| `photo_<Inc:1:1:4>.<Ext>` | Uniform naming with counter |
| `<Name>_<Rand:100:999>.<Ext>` | Append random suffix |

Unrecognized tags (e.g. `<Foo>`) are left as literal text, angle brackets included.

---

### Trim

Remove specific characters from the edges of the filename.

| Parameter | Options | Default | Description |
|---|---|---|---|
| Chars | | (space) | Characters to trim. Each character in this field is trimmed individually. |
| Location | Start, End, Both | Both | Which edge(s) to trim from. |
| Scope | Name only, Extension only, Full name | Name only | Which part of the filename to trim. |

Trimming continues inward from the specified edge(s) until a character not in the trim set is found.

For example, with Chars `_-` on filename `__-file-name-_.txt` (Name only scope):
- Start: `-file-name-_.txt`
- End: `__-file-name.txt`
- Both: `file-name.txt`

**Examples:**

| Files | Chars | Location | Scope | Result |
|---|---|---|---|---|
| `  photo  .jpg` | (space) | Both | Name only | `photo.jpg` |
| `___report___.pdf` | `_` | Both | Name only | `report.pdf` |
| `--title--.txt` | `-` | Both | Name only | `title.txt` |
| `file.txt   ` | (space) | End | Full name | `file.txt` |
| `000123.txt` | `0` | Start | Name only | `123.txt` |

---

## Method Stack

Instead of applying a single operation, you build a **sequence of methods** that are applied one after another to each filename. The output of each method becomes the input for the next.

### Example

Starting files: `IMG_0001.JPG`, `IMG_0002.JPG`, `IMG_0003.JPG`

| Step | Method | File 1 | File 2 | File 3 |
|---|---|---|---|---|
| Original | | `IMG_0001.JPG` | `IMG_0002.JPG` | `IMG_0003.JPG` |
| 1 | Replace: `IMG_` → `Photo_` | `Photo_0001.JPG` | `Photo_0002.JPG` | `Photo_0003.JPG` |
| 2 | New Case: lowercase, ext only | `Photo_0001.jpg` | `Photo_0002.jpg` | `Photo_0003.jpg` |
| 3 | Replace: `_0` → `_` | `Photo_1.jpg` | `Photo_2.jpg` | `Photo_3.jpg` |

### Managing the Stack

- **Add** — configure a method and click the Add button to commit it to the end of the stack
- **Remove** — click **x** next to a method to remove it
- **Move up / down** — click **^** or **v** to change a method's position in the sequence

The order matters. For example, "Replace `img` with `photo`" (case insensitive) followed by "lowercase" is different from "lowercase" followed by "Replace `img` with `photo`" — the latter wouldn't match `IMG` because it has already been lowered.

---

## Live Preview

The preview updates in real time:

- While you type in the parameter form (before clicking Add), the preview shows what the result would look like if that method were added to the stack
- After adding a method, the preview reflects the committed stack and the editing form resets
- Every keystroke and every change in dropdowns or checkboxes triggers an instant preview update

---

## Conflicts and Errors

### Conflicts

A conflict occurs when two or more files would end up with the **same name** in the same directory. On Windows, the comparison is case-insensitive; on Linux, it is case-sensitive.

- Conflicting rows are highlighted in **red**
- The **Rename** button is disabled until all conflicts are resolved
- To resolve, add a counter (`<Inc:...>`) or adjust methods to differentiate names

### Errors

An error occurs when the new name contains **invalid characters** for the current OS or is empty.

- Invalid characters on Windows: `\ / : * ? " < > |`
- Invalid characters on Linux: `/`
- Error rows are shown with the new name **struck through in gray**
- The **Rename** button is disabled until all errors are resolved

---

## Executing the Rename

The **Rename** button becomes active when all of the following are true:

- At least one method is in the stack
- No conflicts
- No errors
- At least one file has a new name different from the original

On success, the dialog closes and both panels refresh automatically. If some renames fail (e.g. permission denied), the dialog stays open and shows how many files were renamed and how many failed. Failed files are rolled back to their original names.

---

## Keyboard Shortcuts

| Key | Action |
|---|---|
| **Ctrl+F2** | Open Advanced Rename (from main view) |
| **Escape** | Close the dialog |
