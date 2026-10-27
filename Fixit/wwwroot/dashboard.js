(async () => {
    const role = document.body.dataset.role;
    const isAdmin = role === 'admin';
    const message = document.getElementById('dashboard-message');
    let session;
    try {
        const response = await fetch('/api/me', { headers: { Accept: 'application/json' } });
        if (response.status === 401) {
            window.location.replace('index.html');
            return;
        }
        if (!response.ok) throw new Error(`Account verification failed (HTTP ${response.status}).`);
        session = await response.json();
        if (!['user', 'admin'].includes(session.role)) {
            throw new Error('Your account has an unsupported role. Contact the school administrator.');
        }
        if (session.role !== role) {
            window.location.replace(session.role === 'admin' ? 'dashboard_admin.html' : 'dashboard_user.html');
            return;
        }
    } catch (error) {
        message.textContent = `Could not verify your account: ${error.message}`;
        message.classList.add('is-error');
        message.hidden = false;
        return;
    }

    const reportsKey = 'fixit-maintenance-reports';
    const profileKey = `fixit-profile-name-${role}`;
    const clientKey = 'fixit-client-id';
    const statusOptions = ['New', 'Seen', 'In progress', 'Fixed', 'Closed'];
    const resolvedStatuses = ['Fixed', 'Closed'];
    const activeStatuses = ['New', 'Seen', 'In progress'];
    const defaultName = session.username || (isAdmin ? 'Maintenance team' : 'Campus member');
    const grid = document.getElementById('report-grid');
    const addReportButton = document.getElementById('add-report-button');
    const reportDialog = document.getElementById('report-dialog');
    const settingsDialog = document.getElementById('settings-dialog');
    const searchInput = document.getElementById('report-search');
    const statusFilter = document.getElementById('status-filter');
    const locationFilter = document.getElementById('location-filter');
    const sortFilter = document.getElementById('sort-filter');
    let clientId = '';
    let activeTab = 'all';
    let reports = [];
    let storageError = '';

    function escapeHtml(value) {
        return String(value ?? '').replace(/[&<>"']/g, character => ({
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#39;'
        })[character]);
    }

    function readProfileName() {
        return localStorage.getItem(profileKey) || defaultName;
    }

    function getClientId() {
        let clientId = localStorage.getItem(clientKey);
        if (!clientId) {
            clientId = window.crypto?.randomUUID
                ? window.crypto.randomUUID()
                : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
            localStorage.setItem(clientKey, clientId);
        }
        return clientId;
    }

    function saveReports(nextReports) {
        localStorage.setItem(reportsKey, JSON.stringify(nextReports));
        reports = nextReports;
        render();
    }

    function loadReports() {
        const savedReports = localStorage.getItem(reportsKey);
        if (!savedReports) return [];
        const parsed = JSON.parse(savedReports);
        if (!Array.isArray(parsed) || parsed.some(report =>
            !report || typeof report.id !== 'string' ||
            typeof report.title !== 'string' ||
            typeof report.location !== 'string' ||
            typeof report.description !== 'string' ||
            typeof report.reporter !== 'string' ||
            typeof report.ownerId !== 'string' ||
            typeof report.createdAt !== 'string' ||
            !statusOptions.includes(report.status))) {
            throw new Error('Saved reports are in an unexpected format.');
        }
        return parsed;
    }

    function showMessage(text, isError = false) {
        message.textContent = text;
        message.classList.toggle('is-error', isError);
        message.hidden = !text;
    }

    function updateProfile() {
        const name = readProfileName();
        const initial = name.trim().charAt(0).toUpperCase() || 'C';
        document.getElementById('profile-name').textContent = name;
        document.getElementById('menu-name').textContent = name;
        document.getElementById('profile-avatar').textContent = initial;
        document.getElementById('menu-avatar').textContent = initial;
    }

    function isResolved(report) {
        return resolvedStatuses.includes(report.status);
    }

    function matchesTab(report) {
        if (activeTab === 'mine') return report.ownerId === clientId;
        if (activeTab === 'attention') return ['New', 'Seen'].includes(report.status);
        if (activeTab === 'progress') return report.status === 'In progress';
        if (activeTab === 'resolved') return isResolved(report);
        return true;
    }

    function getVisibleReports() {
        const query = searchInput.value.trim().toLowerCase();
        const location = locationFilter.value.trim().toLowerCase();
        return reports
            .filter(matchesTab)
            .filter(report => !statusFilter.value || report.status === statusFilter.value)
            .filter(report => !location || report.location.toLowerCase().includes(location))
            .filter(report => !query || [report.title, report.location, report.description, report.reporter]
                .some(value => value.toLowerCase().includes(query)))
            .sort((first, second) => sortFilter.value === 'oldest'
                ? first.createdAt.localeCompare(second.createdAt)
                : second.createdAt.localeCompare(first.createdAt));
    }

    function renderCard(report) {
        const date = new Date(report.createdAt);
        const formattedDate = Number.isNaN(date.getTime())
            ? 'Date unavailable'
            : new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' }).format(date);
        const image = report.image
            ? `<img class="report-image" src="${escapeHtml(report.image)}" alt="Photo attached to ${escapeHtml(report.title)}">`
            : '';
        const adminControls = isAdmin
            ? `<details class="report-actions">
                    <summary aria-label="Manage ${escapeHtml(report.title)}">Manage</summary>
                    <div class="report-action-panel">
                        <label>Status
                            <select data-action="status" data-report-id="${escapeHtml(report.id)}">
                                ${statusOptions.map(status => `<option${status === report.status ? ' selected' : ''}>${status}</option>`).join('')}
                            </select>
                        </label>
                        <label>Assigned to
                            <input data-action="assignee" data-report-id="${escapeHtml(report.id)}" value="${escapeHtml(report.assignee || '')}" maxlength="60" placeholder="Unassigned">
                        </label>
                        <button class="delete-report-button" type="button" data-action="delete" data-report-id="${escapeHtml(report.id)}">Delete report</button>
                    </div>
                </details>`
            : '';

        return `<article class="report-card">
            ${image}
            <div class="report-card-content">
                <div class="report-card-top">
                    <span class="status-badge status-${report.status.toLowerCase().replaceAll(' ', '-')}">${escapeHtml(report.status)}</span>
                    ${adminControls}
                </div>
                <h3>${escapeHtml(report.title)}</h3>
                <p class="report-location"><svg viewBox="0 0 20 20" aria-hidden="true"><path d="M16 8.3c0 4.1-6 9-6 9s-6-4.9-6-9a6 6 0 1 1 12 0Z"/><circle cx="10" cy="8" r="2"/></svg>${escapeHtml(report.location)}</p>
                <p class="report-description">${escapeHtml(report.description)}</p>
                <div class="report-card-footer">
                    <span>${isAdmin ? `Reported by ${escapeHtml(report.reporter)}` : `Submitted ${formattedDate}`}</span>
                    <time datetime="${escapeHtml(report.createdAt)}">${formattedDate}</time>
                </div>
                ${isAdmin && report.assignee ? `<p class="report-assignee">Assigned to <strong>${escapeHtml(report.assignee)}</strong></p>` : ''}
            </div>
        </article>`;
    }

    function render() {
        const ownedReports = reports.filter(report => report.ownerId === clientId);
        const resolvedCount = isAdmin
            ? reports.filter(isResolved).length
            : ownedReports.filter(isResolved).length;
        const activeCount = isAdmin
            ? reports.filter(report => activeStatuses.includes(report.status)).length
            : ownedReports.filter(report => activeStatuses.includes(report.status)).length;
        const totalCount = isAdmin ? reports.length : ownedReports.length;

        document.getElementById('stat-total').textContent = totalCount;
        document.getElementById('stat-active').textContent = activeCount;
        document.getElementById('stat-resolved').textContent = resolvedCount;
        document.getElementById('tab-count-all').textContent = isAdmin ? reports.length : ownedReports.length;
        grid.innerHTML = getVisibleReports().map(renderCard).join('');
        if (grid.childElementCount === 0) {
            const filtered = searchInput.value || statusFilter.value || locationFilter.value ||
                activeTab !== 'all';
            const title = filtered ? 'No matching reports' : 'A little care starts with one report.';
            const description = filtered
                ? 'Try another search or clear the filters to see more.'
                : isAdmin
                    ? 'New campus maintenance reports will show up here.'
                    : 'Notice something that needs attention? Send your first report and help keep campus in good shape.';
            grid.innerHTML = `<div class="empty-state"><span class="empty-state-icon" aria-hidden="true">✦</span><h3>${title}</h3><p>${description}</p>${!isAdmin && !filtered ? '<button type="button" class="add-report-button" data-empty-add>+ New report</button>' : ''}</div>`;
        }
    }

    function persist(nextReports, successMessage) {
        try {
            saveReports(nextReports);
            showMessage(successMessage);
        } catch (error) {
            showMessage(`Could not save this change in your browser: ${error.message}`, true);
        }
    }

    try {
        clientId = getClientId();
        reports = loadReports();
        updateProfile();
    } catch (error) {
        storageError = error.message;
        showMessage(`Reports could not be loaded: ${storageError}`, true);
        addReportButton?.setAttribute('disabled', '');
        document.getElementById('profile-name').textContent = defaultName;
        document.getElementById('menu-name').textContent = defaultName;
        document.getElementById('profile-avatar').textContent = defaultName.charAt(0);
        document.getElementById('menu-avatar').textContent = defaultName.charAt(0);
    }
    render();

    document.getElementById('profile-toggle').addEventListener('click', event => {
        const button = event.currentTarget;
        const menu = document.getElementById('profile-menu');
        const isExpanded = button.getAttribute('aria-expanded') === 'true';
        button.setAttribute('aria-expanded', String(!isExpanded));
        menu.hidden = isExpanded;
    });

    document.addEventListener('click', event => {
        const profileArea = document.querySelector('.profile-area');
        if (!profileArea.contains(event.target)) {
            document.getElementById('profile-menu').hidden = true;
            document.getElementById('profile-toggle').setAttribute('aria-expanded', 'false');
        }
        const addButton = event.target.closest('[data-empty-add]');
        if (addButton && !storageError) reportDialog.showModal();
    });

    document.querySelector('[data-open-settings]').addEventListener('click', () => {
        document.getElementById('profile-menu').hidden = true;
        document.getElementById('profile-toggle').setAttribute('aria-expanded', 'false');
        document.getElementById('settings-name').value = readProfileName();
        document.getElementById('settings-error').textContent = '';
        settingsDialog.showModal();
    });

    document.querySelectorAll('[data-close-dialog]').forEach(button => {
        button.addEventListener('click', () => button.closest('dialog').close());
    });
    document.querySelectorAll('dialog').forEach(dialog => {
        dialog.addEventListener('click', event => {
            if (event.target === dialog) dialog.close();
        });
    });

    document.getElementById('settings-form').addEventListener('submit', event => {
        event.preventDefault();
        const name = document.getElementById('settings-name').value.trim();
        if (!name) {
            document.getElementById('settings-error').textContent = 'Enter a display name to save your settings.';
            return;
        }
        try {
            localStorage.setItem(profileKey, name);
            updateProfile();
            settingsDialog.close();
            showMessage('Your display name has been updated.');
        } catch (error) {
            document.getElementById('settings-error').textContent = `Could not save your settings: ${error.message}`;
        }
    });

    document.querySelector('[data-logout]').addEventListener('click', async () => {
        const button = document.querySelector('[data-logout]');
        button.disabled = true;
        try {
            const response = await fetch('/api/logout', { method: 'POST' });
            if (!response.ok) throw new Error(`The server could not log you out (HTTP ${response.status}).`);
            window.location.href = 'index.html';
        } catch (error) {
            showMessage(`Could not log out: ${error.message}`, true);
            button.disabled = false;
        }
    });

    document.querySelectorAll('.report-tab').forEach(button => {
        button.addEventListener('click', () => {
            activeTab = button.dataset.tab;
            document.querySelectorAll('.report-tab').forEach(tab => tab.classList.toggle('is-active', tab === button));
            render();
        });
    });

    const filterToggle = document.getElementById('filter-toggle');
    filterToggle.addEventListener('click', () => {
        const panel = document.getElementById('filter-panel');
        const expanded = filterToggle.getAttribute('aria-expanded') === 'true';
        filterToggle.setAttribute('aria-expanded', String(!expanded));
        panel.hidden = expanded;
    });

    [searchInput, statusFilter, locationFilter, sortFilter].forEach(input => {
        input.addEventListener('input', render);
        input.addEventListener('change', render);
    });
    document.getElementById('clear-filters').addEventListener('click', () => {
        searchInput.value = '';
        statusFilter.value = '';
        locationFilter.value = '';
        sortFilter.value = 'newest';
        render();
    });

    grid.addEventListener('change', event => {
        const control = event.target.closest('[data-action="status"], [data-action="assignee"]');
        if (!control) return;
        const report = reports.find(item => item.id === control.dataset.reportId);
        if (!report) return;
        const nextReports = reports.map(item => item.id !== report.id ? item : {
            ...item,
            ...(control.dataset.action === 'status'
                ? { status: control.value }
                : { assignee: control.value.trim() })
        });
        persist(nextReports, control.dataset.action === 'status' ? 'Report status updated.' : 'Report assignment updated.');
    });

    grid.addEventListener('click', event => {
        const button = event.target.closest('[data-action="delete"]');
        if (!button) return;
        const report = reports.find(item => item.id === button.dataset.reportId);
        if (!report || !window.confirm(`Delete "${report.title}"? This cannot be undone.`)) return;
        persist(reports.filter(item => item.id !== report.id), 'Report deleted.');
    });

    if (addReportButton && reportDialog) {
        addReportButton.addEventListener('click', () => {
            if (storageError) return;
            document.getElementById('report-error').textContent = '';
            reportDialog.showModal();
        });

        document.getElementById('report-form').addEventListener('submit', async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const submitButton = form.querySelector('[type="submit"]');
            const file = document.getElementById('report-image').files[0];
            const errorElement = document.getElementById('report-error');
            errorElement.textContent = '';
            if (file && !['image/jpeg', 'image/png', 'image/gif', 'image/webp'].includes(file.type)) {
                errorElement.textContent = 'Choose a JPEG, PNG, GIF, or WebP image.';
                return;
            }
            if (file && file.size > 1024 * 1024) {
                errorElement.textContent = 'Choose an image smaller than 1 MB.';
                return;
            }

            submitButton.disabled = true;
            try {
                const image = file
                    ? await new Promise((resolve, reject) => {
                        const reader = new FileReader();
                        reader.addEventListener('load', () => resolve(reader.result));
                        reader.addEventListener('error', () => reject(new Error('The image could not be read.')));
                        reader.readAsDataURL(file);
                    })
                    : '';
                const formData = new FormData(form);
                const report = {
                    id: window.crypto?.randomUUID
                        ? window.crypto.randomUUID()
                        : `${Date.now()}-${Math.random().toString(36).slice(2)}`,
                    ownerId: clientId,
                    title: formData.get('title').trim(),
                    location: formData.get('location').trim(),
                    description: formData.get('description').trim(),
                    reporter: readProfileName(),
                    status: 'New',
                    assignee: '',
                    createdAt: new Date().toISOString(),
                    image
                };
                persist([report, ...reports], 'Your report has been submitted.');
                if (!message.classList.contains('is-error')) {
                    form.reset();
                    reportDialog.close();
                }
            } catch (error) {
                errorElement.textContent = `Could not submit the report: ${error.message}`;
            } finally {
                submitButton.disabled = false;
            }
        });
    }
})();
