using System;
using System.Drawing;

namespace DisplayWeave
{
    // Ready: nothing is loaded into the app; it is fitted into its region from outside once maximized.
    public enum HandledAppState { Starting, Working, NotActive, FittedFromOutside, Error, Ready }

    // One app DisplayWeave handles or tried to handle, for status lists.
    public sealed class HandledApp
    {
        public ApplicationIdentity Target;
        public HandledAppState State;
        // The region in screen pixels; empty when none was chosen.
        public Rectangle Region;
        // Why the app is not handled as asked (its component failed, its rule broke); empty otherwise.
        public string Error = String.Empty;
    }
}
