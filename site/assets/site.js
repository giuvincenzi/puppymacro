// Guide: the version select opens the same section in the chosen version.
const versionSelect = document.getElementById('version');
if (versionSelect) {
  versionSelect.addEventListener('change', () => {
    window.location.href = versionSelect.value;
  });
}
