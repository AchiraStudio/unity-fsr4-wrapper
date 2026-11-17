using System;
using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class ScreenController : MonoBehaviour
{
    public LoadingBar controlPanel;
    Action m_Callback;

    EntityId m_LastRtId;
    MeshRenderer m_MeshRenderer;
    Animator m_ScreenAnimator;

    void Start()
    {
        m_MeshRenderer = GetComponent<MeshRenderer>();
        m_ScreenAnimator = GetComponent<Animator>();
    }

    void Update()
    {
        SetScreenRT();
    }

    public void TurnScreenOn()
    {
        SetScreenRT();

        if (m_ScreenAnimator != null)
        {
            m_ScreenAnimator.SetBool("ScreenOn", true);
        }

        if (controlPanel != null)
        {
            controlPanel.TurnOn();
        }

        Shader.SetGlobalColor("_TransitionColor", m_MeshRenderer.material.GetColor("_TransitionEdgeColor"));
    }

    void SetScreenRT()
    {
        if (SceneTransitionManager.IsAvailable())
        {
            var rt = SceneTransitionManager.GetScreenRT();
            if (rt && m_LastRtId != rt.GetEntityId())
            {
                m_LastRtId = rt.GetEntityId();
                m_MeshRenderer.material.SetTexture("_ScreenColor", rt);
            }
        }
    }

    public void TurnScreenOff(Action callback)
    {
        m_Callback = callback;
        if (m_ScreenAnimator != null)
        {
            m_ScreenAnimator.SetBool("ScreenOn", false);
        }

        if (controlPanel != null)
        {
            controlPanel.TurnOff();
        }
    }

    //This is called by the animation clip when the screens have switched off
    public void Callback() => m_Callback?.Invoke();
}
