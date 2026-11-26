export function show(dialog) {
    if (!dialog.__wizionicCancel) {
        dialog.__wizionicCancel = (event) => event.preventDefault();
        dialog.addEventListener("cancel", dialog.__wizionicCancel);
    }
    dialog.showModal();
}
export function close(dialog) { dialog.close(); }
