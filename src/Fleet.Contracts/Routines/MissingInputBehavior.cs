namespace Fleet.Contracts.Routines;

public enum MissingInputBehavior
{
    SkipAndAlert,
    RunWithoutInput,
    Fail,
}
