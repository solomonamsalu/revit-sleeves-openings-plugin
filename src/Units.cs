namespace SleevesOpenings
{
    /// <summary>Revit's internal length unit is feet; the manual (and rules.json) speak in inches.</summary>
    public static class Units
    {
        public static double InchesToFeet(double inches) => inches / 12.0;
        public static double FeetToInches(double feet) => feet * 12.0;

        /// <summary>Formats inches as feet-inches, e.g. 28.5 -> 2'-4.5".</summary>
        public static string FormatInches(double inches)
        {
            int feet = (int)(inches / 12);
            double rem = inches - feet * 12;
            string inch = rem % 1 == 0 ? ((int)rem).ToString() : rem.ToString("0.##");
            return feet > 0 ? feet + "'-" + inch + "\"" : inch + "\"";
        }
    }
}
