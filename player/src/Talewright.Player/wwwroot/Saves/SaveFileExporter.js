export function download(name, data) {
    const url = URL.createObjectURL(new Blob([data], { type: "application/json" }));
    try {
        const link = document.createElement("a");
        link.href = url;
        link.download = name;
        document.body.append(link);
        link.click();
        link.remove();
    } finally {
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
}
