using UnityEngine;
using TLM.TimelineController;

public class Bootstrap : MonoBehaviour
{
    [SerializeField] GameObject cubePrefab;
    [SerializeField] GameObject spherePrefab;
    [SerializeField] GameObject childTimelinePrefab;
    [SerializeField] GameObject timelinePrefab;

    void Start()
    {
        Instantiate(cubePrefab);

        Instantiate(spherePrefab).name = "Sphere1";
        var sphere2 = Instantiate(spherePrefab);
        sphere2.name = "Sphere2";

        Instantiate(childTimelinePrefab);

        var timelineObj = Instantiate(timelinePrefab);
        var timelineController = timelineObj.GetComponent<TimelineController>();
        timelineController.AddRuntimeObject(sphere2);
        timelineController.Play(() => Debug.Log("TimelineController Complete"));
    }
}
