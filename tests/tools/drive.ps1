param([int]$ProcessId, [string]$Window, [string]$Action, [string]$Name)
# Small UI Automation driver: click a button (asynchronously, so modal dialogs don't block) or select a tab.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);'
$A = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]
$cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ProcessId)),
    (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Window)))
$win = $null
for ($i = 0; $i -lt 20 -and -not $win; $i++) { $win = $A::RootElement.FindFirst($scope::Descendants, $cond); if (-not $win) { Start-Sleep -Milliseconds 250 } }
if (-not $win) { "window '$Window' not found"; exit 1 }
$el = $win.FindFirst($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Name)))
if (-not $el) { "element '$Name' not found"; exit 1 }
switch ($Action) {
    'click' { [W.U]::PostMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5 <# BM_CLICK #>, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    'select' { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    'invoke' {
        # WPF buttons have no HWND; walk up from the label to the button and invoke it
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $target = $el
        while ($target -and -not ($target.GetSupportedPatterns() | Where-Object { $_.ProgrammaticName -eq 'InvokePatternIdentifiers.Pattern' })) { $target = $walker.GetParent($target) }
        $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
}
"$Action '$Name' ok"
