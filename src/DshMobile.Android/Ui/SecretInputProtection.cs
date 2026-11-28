using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;

namespace DshMobile.App.Ui;

/// <summary>
/// 秘密输入保护：向无障碍服务隐藏实际文本，并阻止自动填充服务读取敏感字段。
/// </summary>
internal static class SecretInputProtection
{
    private static readonly SecretAccessibilityDelegate SharedDelegate = new();

    /// <summary>
    /// 为秘密输入框启用保护：允许自动填充识别字段，但由无障碍委托隐藏真实内容。
    /// </summary>
    public static void AttachSecretInput(TextView editText)
    {
        editText.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
        editText.SetAccessibilityDelegate(SharedDelegate);
    }

    /// <summary>
    /// 解除秘密输入保护，恢复默认的自动填充与无障碍行为。
    /// </summary>
    public static void DetachSecretInput(TextView editText)
    {
        editText.ImportantForAutofill = ImportantForAutofill.Auto;
        editText.SetAccessibilityDelegate(null);
    }

    /// <summary>
    /// 为对话框窗口添加安全标志，阻止窗口内容出现在截图与最近任务预览中。
    /// </summary>
    public static void ApplySecureWindowFlag(Android.App.Dialog dialog)
    {
        dialog.Window?.AddFlags(WindowManagerFlags.Secure);
    }

    /// <summary>
    /// 清除对话框窗口的安全标志，恢复正常截图与预览行为。
    /// </summary>
    public static void ClearSecureWindowFlag(Android.App.Dialog dialog)
    {
        dialog.Window?.ClearFlags(WindowManagerFlags.Secure);
    }

    /// <summary>
    /// 清空输入框中的文本与可编辑内容。
    /// </summary>
    public static void ClearEditText(EditText? editText)
    {
        if (editText is null)
        {
            return;
        }

        try
        {
            editText.Text = string.Empty;
            editText.EditableText?.Clear();
        }
        catch (Exception)
        {
            // 控件已分离或正在销毁时清空可能失败：只保证不把异常带到调用方。
        }
    }

    /// <summary>
    /// 无障碍委托：屏蔽秘密输入框的真实文本、内容描述与无障碍事件文本。
    /// </summary>
    private sealed class SecretAccessibilityDelegate : View.AccessibilityDelegate
    {
        public override void OnInitializeAccessibilityNodeInfo(View host, AccessibilityNodeInfo info)
        {
            base.OnInitializeAccessibilityNodeInfo(host, info);

            // 将节点标记为密码字段，并使用等长掩码替代真实文本。
            info.Password = true;
            var textLength = (host as TextView)?.Text?.Length ?? 0;
            info.Text = textLength > 0 ? new string('•', textLength) : null;
            info.ContentDescription = null;
        }

        public override void OnInitializeAccessibilityEvent(View host, AccessibilityEvent e)
        {
            base.OnInitializeAccessibilityEvent(host, e);

            // 无障碍事件同样标记为密码，并清除可能携带的明文。
            e.Password = true;
            e.Text?.Clear();
        }
    }
}
