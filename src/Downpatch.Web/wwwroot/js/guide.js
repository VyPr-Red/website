window.initializeGuideTOC = () => {

    const headings = [...document.querySelectorAll("article h2[id], article h3[id]")];

    if (!headings.length)
        return;

    const links = new Map();

    for (const h of headings) {

        const link = document.getElementById("toc-" + h.id);

        if (link)
            links.set(h.id, link);

    }

    const observer = new IntersectionObserver(entries => {

        entries.forEach(entry => {

            if (!entry.isIntersecting)
                return;

            for (const l of links.values())
                l.classList.remove("active");

            const active = links.get(entry.target.id);

            active?.classList.add("active");

        });

    }, {

        rootMargin: "0px 0px -70% 0px",
        threshold: 0

    });

    headings.forEach(h => observer.observe(h));

};