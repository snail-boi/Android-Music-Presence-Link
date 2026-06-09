namespace musicpresense
{
    /// <summary>
    /// One row in the onboarding sidebar. The sidebar list is rebuilt wholesale on every step
    /// change, so these are plain values with no change notification needed. The colour
    /// properties are hex strings bound to Brush targets in the DataTemplate; WPF converts the
    /// strings to brushes automatically.
    /// </summary>
    internal sealed class SidebarStepItem
    {
        public string Title { get; set; } = string.Empty;
        public string NumberText { get; set; } = string.Empty;
        public string NumberBackground { get; set; } = "#33FFFFFF";
        public string NumberForeground { get; set; } = "#FFFFFF";
        public string RowBackground { get; set; } = "#00000000";
        public double TitleOpacity { get; set; } = 0.7;
        public string TitleWeight { get; set; } = "Normal";
    }
}
