using UnityEngine;
using UnityEngine.Experimental.Rendering;

public class ScreenCamera : MonoBehaviour
{
    [SerializeField]
    float m_RenderTextureScale = 1;

    Camera m_Camera;
    int m_LastScreenWidth, m_LastScreenHeight;

    void Awake()
    {
        m_Camera = GetComponent<Camera>();
        UpdateTarget();
    }

    void Update()
    {
        if (Screen.width != m_LastScreenWidth ||  Screen.height != m_LastScreenHeight)
            UpdateTarget();
    }

    void UpdateTarget()
    {
        var targetTexture = m_Camera.targetTexture;
        if (targetTexture != null)
        {
            targetTexture.Release();
            Destroy(targetTexture);
        }

        var w = (int)(Screen.width *  m_RenderTextureScale);
        var h = (int)(Screen.height * m_RenderTextureScale);
        targetTexture = new RenderTexture(w, h, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.D24_UNorm_S8_UInt);
        targetTexture.Create();
        m_Camera.targetTexture = targetTexture;

        m_LastScreenWidth = Screen.width;
        m_LastScreenHeight = Screen.height;
    }
}
