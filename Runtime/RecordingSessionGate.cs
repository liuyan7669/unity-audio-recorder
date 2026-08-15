namespace Cowart.AudioRecorder
{
    internal sealed class RecordingSessionGate
    {
        private int nextGeneration;
        private int activeGeneration;
        private bool isFinalizing;
        private bool terminalPublished;

        internal int ActiveGeneration => activeGeneration;

        internal bool IsActive => activeGeneration != 0 && !terminalPublished;

        internal bool IsFinalizing => IsActive && isFinalizing;

        internal int Begin()
        {
            if (IsActive)
            {
                return 0;
            }

            do
            {
                activeGeneration = unchecked(++nextGeneration);
            }
            while (activeGeneration == 0);

            isFinalizing = false;
            terminalPublished = false;
            return activeGeneration;
        }

        internal bool IsCurrent(int generation)
        {
            return generation != 0 &&
                   generation == activeGeneration &&
                   !terminalPublished;
        }

        internal bool TryBeginFinalizing(int generation)
        {
            if (!IsCurrent(generation))
            {
                return false;
            }

            isFinalizing = true;
            return true;
        }

        internal bool TryPublishTerminal(int generation)
        {
            if (!IsCurrent(generation))
            {
                return false;
            }

            terminalPublished = true;
            isFinalizing = false;
            activeGeneration = 0;
            return true;
        }

        internal void AbortActive()
        {
            terminalPublished = true;
            isFinalizing = false;
            activeGeneration = 0;
        }
    }
}
