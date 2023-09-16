using Microsoft.AspNetCore.Components;

namespace BlazorComponent.Custom;

public class CounterViewModel:BaseComponent
{
    protected int currentCount = 0;

    [Inject]
    public IService Service { get; set; }
    protected void IncrementCount()
    {
        Service.Hello();
        currentCount++;
    }
}