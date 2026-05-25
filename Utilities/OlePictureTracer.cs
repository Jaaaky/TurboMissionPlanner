using System;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Phase 10o fork: surgical tracer for the OLE IPicture fixme storm on
    /// Wine. The CLR's COM Callable Wrapper probes a fixed set of interfaces
    /// (IManagedObject, IAgileObject, IMarshal, INoMarshal, IInspectable,
    /// IProvideClassInfo, IRpcOptions) every time it marshals a new
    /// .NET Image through OleCreatePictureIndirect into an OLE IPicture.
    /// Wine logs each unsupported probe as
    ///   OLEPictureImpl_QueryInterface () : asking for unsupported interface
    /// Multiplied by every PictureBox.Image / ToolStripItem.Image / Cursor /
    /// BackgroundImage etc. assignment that ever happens, it floods stderr.
    ///
    /// Rather than blindly silencing it (e.g. WINEDEBUG=-fixme), we trace
    /// the actual MissionPlanner-side triggers so the source can be fixed at
    /// the WinForms level (replacing Image with native Bitmap drawing, or
    /// guarding the assignment). Each entry tells the developer EXACTLY
    /// which control's Image property is the trigger.
    ///
    /// Enable via:
    ///   MP_OLE_TRACE=1 env var, OR
    ///   Settings["EnableOlePictureTracer"]=true
    /// Output: log4net Info ("OlePictureTracer" logger) + stdout.
    /// Call OlePictureTracer.ScanAndLog(form) once after InitializeComponent.
    /// </summary>
    public static class OlePictureTracer
    {
        private static readonly ILog log =
            LogManager.GetLogger("OlePictureTracer");

        public static bool Enabled { get; private set; }

        static OlePictureTracer()
        {
            try
            {
                bool envOn = string.Equals(
                    Environment.GetEnvironmentVariable("MP_OLE_TRACE"),
                    "1", StringComparison.Ordinal);
                bool settingOn = false;
                try { settingOn = Settings.Instance.GetBoolean("EnableOlePictureTracer"); }
                catch { }
                Enabled = envOn || settingOn;
            }
            catch { Enabled = false; }
        }

        /// <summary>
        /// Walk the control tree from root and log every property setter
        /// whose value is non-null and whose type is in the "triggers OLE
        /// IPicture marshalling" bucket. Includes the full control path so
        /// developer can pinpoint the source.
        /// </summary>
        public static void ScanAndLog(Control root)
        {
            if (!Enabled || root == null) return;
            try
            {
                int totalImages = 0;
                ScanRecursive(root, new StringBuilder(root.Name ?? root.GetType().Name), ref totalImages, 0);
                var msg = string.Format(
                    "OlePictureTracer scan complete: {0} image-bearing properties found under {1}. " +
                    "Each one is a potential OLE IPicture marshalling trigger on Wine.",
                    totalImages, root.GetType().FullName);
                Console.WriteLine("[OlePictureTracer] " + msg);
                log.Info(msg);
            }
            catch (Exception ex)
            {
                log.Warn("OlePictureTracer scan failed", ex);
            }
        }

        private static void ScanRecursive(Control c, StringBuilder path, ref int count, int depth)
        {
            if (c == null || depth > 30) return;

            // Properties on Control + its subclasses worth probing. We
            // reflect rather than hardcode per-type so e.g. ToolStripButton,
            // PictureBox, CheckBox, RadioButton, Button.Image, BackgroundImage,
            // ErrorIcon, Cursor (when set non-default), TreeNode images via
            // ImageList, etc. all get caught.
            LogPropIfImage(c, "Image", path, ref count);
            LogPropIfImage(c, "BackgroundImage", path, ref count);
            LogPropIfImage(c, "ErrorImage", path, ref count);
            LogPropIfImage(c, "InitialImage", path, ref count);

            // ImageList enumeration - one ImageList holds many images and
            // each can become an IPicture if hosted in an old container.
            var imgListProp = c.GetType().GetProperty("ImageList",
                BindingFlags.Public | BindingFlags.Instance);
            if (imgListProp != null)
            {
                var lst = imgListProp.GetValue(c, null) as ImageList;
                if (lst != null && lst.Images.Count > 0)
                {
                    count += lst.Images.Count;
                    Console.WriteLine("[OlePictureTracer] {0}: ImageList holds {1} images",
                        path, lst.Images.Count);
                    log.InfoFormat("{0}: ImageList[{1}]", path, lst.Images.Count);
                }
            }

            // ToolStrip and MenuStrip own items that have their own .Image.
            if (c is ToolStrip ts)
            {
                foreach (ToolStripItem item in ts.Items)
                    ScanToolStripItem(item, path, ref count);
            }

            // Recurse.
            int basePathLen = path.Length;
            foreach (Control child in c.Controls)
            {
                path.Append('/');
                path.Append(string.IsNullOrEmpty(child.Name) ? child.GetType().Name : child.Name);
                ScanRecursive(child, path, ref count, depth + 1);
                path.Length = basePathLen;
            }
        }

        private static void ScanToolStripItem(ToolStripItem item, StringBuilder path, ref int count)
        {
            if (item == null) return;
            if (item.Image != null)
            {
                count++;
                Console.WriteLine("[OlePictureTracer] {0}/{1}.Image (ToolStripItem): {2}x{3}",
                    path, item.Name ?? item.GetType().Name,
                    item.Image.Width, item.Image.Height);
                log.InfoFormat("{0}/{1}.Image ToolStripItem", path,
                    item.Name ?? item.GetType().Name);
            }
            // ToolStripDropDownItem may host child items.
            if (item is ToolStripDropDownItem dd)
            {
                foreach (ToolStripItem c in dd.DropDownItems)
                    ScanToolStripItem(c, path, ref count);
            }
        }

        private static void LogPropIfImage(Control c, string propName, StringBuilder path, ref int count)
        {
            try
            {
                var p = c.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return;
                var val = p.GetValue(c, null);
                if (val == null) return;
                // Only count System.Drawing.Image (and subclasses Bitmap/Metafile)
                if (val is System.Drawing.Image img)
                {
                    count++;
                    Console.WriteLine("[OlePictureTracer] {0}.{1} ({2}): {3}x{4} pixelfmt={5}",
                        path, propName, c.GetType().Name, img.Width, img.Height, img.PixelFormat);
                    log.InfoFormat("{0}.{1} {2} {3}x{4}", path, propName,
                        c.GetType().Name, img.Width, img.Height);
                }
            }
            catch { /* property access failed; non-fatal */ }
        }
    }
}
