using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class CameraSwitcher : MonoBehaviour
{
    [SerializeField] private Camera camera1;
    [SerializeField] private Camera camera2;
    [SerializeField] private Camera camera3;

    private Camera[] _cams;
    private int _current;

    void Start()
    {
        if (camera1 == null) camera1 = Camera.main;
        _cams = new[] { camera1, camera2, camera3 };
        _current = 0;
        Apply();
    }

    void Update()
    {
        bool left = false, right = false;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            left = kb.leftArrowKey.wasPressedThisFrame;
            right = kb.rightArrowKey.wasPressedThisFrame;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        left = Input.GetKeyDown(KeyCode.LeftArrow);
        right = Input.GetKeyDown(KeyCode.RightArrow);
#endif
        if (right) Step(1);
        else if (left) Step(-1);
    }

    void Step(int dir)
    {
        for (int i = 1; i <= _cams.Length; i++)
        {
            int next = ((_current + dir * i) % _cams.Length + _cams.Length) % _cams.Length;
            if (_cams[next] == null) continue;
            _current = next;
            break;
        }
        Apply();
    }

    void Apply()
    {
        for (int i = 0; i < _cams.Length; i++)
        {
            var cam = _cams[i];
            if (cam == null) continue;
            cam.enabled = i == _current;
            var listener = cam.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = i == _current;
        }
    }
}
