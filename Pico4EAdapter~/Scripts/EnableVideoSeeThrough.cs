using System.Collections;
using System.Collections.Generic;
using Unity.XR.PXR;
using UnityEngine;

public class EnableVideoSeeThrough : MonoBehaviour
{
    void Start()
    {
        Unity.XR.PXR.PXR_MixedReality.EnableVideoSeeThrough(true);

    }
}
